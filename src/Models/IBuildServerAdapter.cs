using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public interface IBuildServerAdapter
    {
        /// <summary>
        /// Get the display name of this build server type
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Initialize the adapter with build server configuration
        /// </summary>
        void Initialize(BuildServerIntegration config, string repositoryPath);

        /// <summary>
        /// Query build status for a list of commit SHAs
        /// Returns a dictionary mapping commit SHA to build info
        /// </summary>
        Task<Dictionary<string, CommitBuildInfo>> QueryBuildStatusAsync(List<string> commitShas);
    }
}
