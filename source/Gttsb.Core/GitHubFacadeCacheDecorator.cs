using Microsoft.Extensions.Caching.Memory;

namespace Gttsb.Core
{
    public sealed class GitHubFacadeCacheDecorator : IGitHubFacade
    {
        private readonly IGitHubFacade gitHubFacade;
        private readonly IMemoryCache memoryCache;

        public GitHubFacadeCacheDecorator(IGitHubFacade gitHubFacade, IMemoryCache memoryCache)
        {
            this.gitHubFacade = gitHubFacade;
            this.memoryCache = memoryCache;
        }

        public Task<OperationResponse> AddOrgMemberAsync(string gitHubOrg, ValidGitHubId gitHubId) => gitHubFacade.AddOrgMemberAsync(gitHubOrg, gitHubId);

        public Task AddTeamMemberAsync(int teamId, ValidGitHubId userGitHubId) => gitHubFacade.AddTeamMemberAsync(teamId, userGitHubId);

        public Task<GitHubTeam> CreateTeamAsync(string gitHubOrg, string name) => gitHubFacade.CreateTeamAsync(gitHubOrg, name);

        public async Task<ValidGitHubId?> DoesUserExistAsync(string gitHubId)
        {
            // TODO: look up best practices when using a Delegate and Async
            return await memoryCache.GetOrCreateAsync($"DoesUserExistAsync-{gitHubId}", async cacheEntry =>
            {
                cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(10);

                return await gitHubFacade.DoesUserExistAsync(gitHubId);
            });
        }

        public Task<IEnumerable<GitHubTeam>> GetAllTeamsAsync(string org) => gitHubFacade.GetAllTeamsAsync(org);

        public async Task<MemberCheckResult> IsUserMemberAsync(string gitHubOrg, ValidGitHubId gitHubId)
        {
            // TODO: look up best practices when using a Delegate and Async
            return await memoryCache.GetOrCreateAsync($"IsUserMemberAsync-${gitHubOrg}-${gitHubId.Id}", async cacheEntry =>
            {
                cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(10);

                return await gitHubFacade.IsUserMemberAsync(gitHubOrg, gitHubId);
            });
        }
    }
}
