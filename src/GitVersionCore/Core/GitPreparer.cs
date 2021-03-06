using System;
using System.IO;
using System.Linq;
using GitVersion.Extensions;
using GitVersion.Helpers;
using GitVersion.Logging;
using LibGit2Sharp;
using Microsoft.Extensions.Options;

namespace GitVersion
{
    public class GitPreparer : IGitPreparer
    {
        private readonly ILog log;
        private readonly IEnvironment environment;
        private readonly IOptions<Arguments> options;
        private readonly IBuildServerResolver buildServerResolver;

        private const string DefaultRemoteName = "origin";

        public GitPreparer(ILog log, IEnvironment environment, IOptions<Arguments> options, IBuildServerResolver buildServerResolver)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.buildServerResolver = buildServerResolver ?? throw new ArgumentNullException(nameof(buildServerResolver));
        }

        public void Prepare()
        {
            var arguments = options.Value;
            var buildServer = buildServerResolver.Resolve();

            // Normalize if we are running on build server
            var normalizeGitDirectory = !arguments.NoNormalize && buildServer != null;
            var shouldCleanUpRemotes = buildServer != null && buildServer.ShouldCleanUpRemotes();

            var currentBranch = ResolveCurrentBranch(buildServer, arguments.TargetBranch, !string.IsNullOrWhiteSpace(arguments.DynamicRepositoryClonePath));

            PrepareInternal(normalizeGitDirectory, currentBranch, shouldCleanUpRemotes);

            var dotGitDirectory = arguments.DotGitDirectory;
            var projectRoot = arguments.ProjectRootDirectory;

            log.Info($"Project root is: {projectRoot}");
            log.Info($"DotGit directory is: {dotGitDirectory}");
            if (string.IsNullOrEmpty(dotGitDirectory) || string.IsNullOrEmpty(projectRoot))
            {
                throw new Exception($"Failed to prepare or find the .git directory in path '{arguments.TargetPath}'.");
            }
        }

        public void PrepareInternal(bool normalizeGitDirectory, string currentBranch, bool shouldCleanUpRemotes = false)
        {
            var arguments = options.Value;
            var authentication = new AuthenticationInfo
            {
                Username = arguments.Authentication?.Username,
                Password = arguments.Authentication?.Password
            };
            if (!string.IsNullOrWhiteSpace(options.Value.TargetUrl))
            {
                var tempRepositoryPath = CalculateTemporaryRepositoryPath(options.Value.TargetUrl, arguments.DynamicRepositoryClonePath);

                arguments.DynamicGitRepositoryPath = CreateDynamicRepository(tempRepositoryPath, authentication, options.Value.TargetUrl, currentBranch);
            }
            else
            {
                if (normalizeGitDirectory)
                {
                    if (shouldCleanUpRemotes)
                    {
                        CleanupDuplicateOrigin();
                    }

                    NormalizeGitDirectory(authentication, currentBranch, options.Value.DotGitDirectory, options.Value.IsDynamicGitRepository());
                }
            }
        }

        private string ResolveCurrentBranch(IBuildServer buildServer, string targetBranch, bool isDynamicRepository)
        {
            if (buildServer == null)
            {
                return targetBranch;
            }

            var currentBranch = buildServer.GetCurrentBranch(isDynamicRepository) ?? targetBranch;
            log.Info("Branch from build environment: " + currentBranch);

            return currentBranch;
        }

        private void CleanupDuplicateOrigin()
        {
            var remoteToKeep = DefaultRemoteName;

            using var repo = new Repository(options.Value.DotGitDirectory);

            // check that we have a remote that matches defaultRemoteName if not take the first remote
            if (!repo.Network.Remotes.Any(remote => remote.Name.Equals(DefaultRemoteName, StringComparison.InvariantCultureIgnoreCase)))
            {
                remoteToKeep = repo.Network.Remotes.First().Name;
            }

            var duplicateRepos = repo.Network
                                     .Remotes
                                     .Where(remote => !remote.Name.Equals(remoteToKeep, StringComparison.InvariantCultureIgnoreCase))
                                     .Select(remote => remote.Name);

            // remove all remotes that are considered duplicates
            foreach (var repoName in duplicateRepos)
            {
                repo.Network.Remotes.Remove(repoName);
            }
        }

        private static string CalculateTemporaryRepositoryPath(string targetUrl, string dynamicRepositoryClonePath)
        {
            var userTemp = dynamicRepositoryClonePath ?? Path.GetTempPath();
            var repositoryName = targetUrl.Split('/', '\\').Last().Replace(".git", string.Empty);
            var possiblePath = Path.Combine(userTemp, repositoryName);

            // Verify that the existing directory is ok for us to use
            if (Directory.Exists(possiblePath) && !GitRepoHasMatchingRemote(possiblePath, targetUrl))
            {
                var i = 1;
                var originalPath = possiblePath;
                bool possiblePathExists;
                do
                {
                    possiblePath = string.Concat(originalPath, "_", i++.ToString());
                    possiblePathExists = Directory.Exists(possiblePath);
                } while (possiblePathExists && !GitRepoHasMatchingRemote(possiblePath, targetUrl));
            }

            return possiblePath;
        }

        private static bool GitRepoHasMatchingRemote(string possiblePath, string targetUrl)
        {
            try
            {
                using var repository = new Repository(possiblePath);
                return repository.Network.Remotes.Any(r => r.Url == targetUrl);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private string CreateDynamicRepository(string targetPath, AuthenticationInfo auth, string repositoryUrl, string targetBranch)
        {
            if (string.IsNullOrWhiteSpace(targetBranch))
            {
                throw new Exception("Dynamic Git repositories must have a target branch (/b)");
            }

            using (log.IndentLog($"Creating dynamic repository at '{targetPath}'"))
            {
                var gitDirectory = Path.Combine(targetPath, ".git");
                if (!Directory.Exists(targetPath))
                {
                    CloneRepository(repositoryUrl, gitDirectory, auth);
                }
                else
                {
                    log.Info("Git repository already exists");
                }
                NormalizeGitDirectory(auth, targetBranch, gitDirectory, true);
                return gitDirectory;
            }
        }

        private void NormalizeGitDirectory(AuthenticationInfo auth, string targetBranch, string gitDirectory, bool isDynamicRepository)
        {
            using (log.IndentLog($"Normalizing git directory for branch '{targetBranch}'"))
            {
                // Normalize (download branches) before using the branch
                GitRepositoryHelper.NormalizeGitDirectory(log, environment, gitDirectory, auth, options.Value.NoFetch, targetBranch, isDynamicRepository);
            }
        }

        private void CloneRepository(string repositoryUrl, string gitDirectory, AuthenticationInfo auth)
        {
            Credentials credentials = null;

            if (auth != null)
            {
                if (!string.IsNullOrWhiteSpace(auth.Username) && !string.IsNullOrWhiteSpace(auth.Password))
                {
                    log.Info($"Setting up credentials using name '{auth.Username}'");

                    credentials = new UsernamePasswordCredentials
                    {
                        Username = auth.Username,
                        Password = auth.Password
                    };
                }
            }

            try
            {
                using (log.IndentLog($"Cloning repository from url '{repositoryUrl}'"))
                {
                    var cloneOptions = new CloneOptions
                    {
                        Checkout = false,
                        CredentialsProvider = (url, usernameFromUrl, types) => credentials
                    };

                    var returnedPath = Repository.Clone(repositoryUrl, gitDirectory, cloneOptions);
                    log.Info($"Returned path after repository clone: {returnedPath}");
                }
            }
            catch (LibGit2SharpException ex)
            {
                var message = ex.Message;
                if (message.Contains("401"))
                {
                    throw new Exception("Unauthorized: Incorrect username/password");
                }
                if (message.Contains("403"))
                {
                    throw new Exception("Forbidden: Possibly Incorrect username/password");
                }
                if (message.Contains("404"))
                {
                    throw new Exception("Not found: The repository was not found");
                }

                throw new Exception("There was an unknown problem with the Git repository you provided", ex);
            }
        }
    }
}
