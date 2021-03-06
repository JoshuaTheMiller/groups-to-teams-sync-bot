namespace Gttsb.Core
{
    internal sealed class GroupSyncer : IGroupSyncer
    {
        private readonly IActiveDirectoryFacade _activeDirectoryFacade;
        private readonly IGitHubFacade _gitHubFacade;
        private readonly IEmailToCloudIdConverter _emailToGitHubIdConverter;

        public GroupSyncer(IActiveDirectoryFacade activeDirectoryFacade, IGitHubFacade gitHubFacade, IEmailToCloudIdConverter emailToCloudIdConverter)
        {
            _activeDirectoryFacade = activeDirectoryFacade;
            _gitHubFacade = gitHubFacade;
            _emailToGitHubIdConverter = emailToCloudIdConverter;
        }

        public Task<GroupSyncResult> SyncronizeGroupsAsync(string gitHubOrg, IEnumerable<TeamDefinition> teams) => SyncronizeGroupsAsync(gitHubOrg, teams, false);

        public Task<GroupSyncResult> SyncronizeMembersAsync(string gitHubOrg, TeamDefinition team) => SyncronizeGroupsAsync(gitHubOrg, new[] { team }, true);

        // TODO: clean this up... This method could be doing too much.
        private async Task<GroupSyncResult> SyncronizeGroupsAsync(string gitHubOrg, IEnumerable<TeamDefinition> teams, bool addMembers = false)
        {
            var teamSyncFailures = new List<string>();
            var usersWithSyncIssues = new List<GitHubUser>();

            // TODO: would be better to have a _gitHubTeamService expose a method of GetTeamDetailsAsync
            // where if would retrieve or create a team and return the details of that instead of exposing
            // GetAllTeams and having this GroupSyncer have the responsibility of filtering the teams
            // and using them here.
            var allTeams = await _gitHubFacade.GetAllTeamsAsync(gitHubOrg);

            foreach (var team in teams)
            {
                var specificTeam = allTeams.FirstOrDefault(t => t.Name == team.Name);

                if (specificTeam == null)
                {
                    specificTeam = await _gitHubFacade.CreateTeamAsync(gitHubOrg, team.Name);
                }

                var membersResponse = await _activeDirectoryFacade.FetchMembersAsync(team.Name);

                if (!membersResponse.Success)
                {
                    teamSyncFailures.Add(team.Name);
                    // try syncing other teams
                    continue;
                }

                var groupMembersWithGitHubIds = membersResponse.Members.Select(m => new
                {
                    m.Id,
                    m.DisplayName,
                    m.Email,
                    GitHubId = _emailToGitHubIdConverter.ToId(m.Email)
                });

                foreach (var user in groupMembersWithGitHubIds)
                {

                    var validUser = await _gitHubFacade.DoesUserExistAsync(user.GitHubId);

                    if (validUser == null)
                    {
                        usersWithSyncIssues.Add(new GitHubUser
                        (
                            Email: user.Email,
                            GitHubId: user.GitHubId
                        ));
                        continue;
                    }

                    await _gitHubFacade.AddTeamMemberAsync(specificTeam.Id, validUser);

                    if(!addMembers)
                    {
                        // If addMembers is not set to true, we do 
                        // not want to add the User as a Member to the Organization
                        continue;
                    }

                    var result = await _gitHubFacade.IsUserMemberAsync(gitHubOrg, validUser);                    

                    if (result == MemberCheckResult.IsNotOrgMember)
                    {
                        var status = await _gitHubFacade.AddOrgMemberAsync(gitHubOrg, validUser);                 
                    }                                        
                }
            }

            return new GroupSyncResult(usersWithSyncIssues);
        }
    }
}
