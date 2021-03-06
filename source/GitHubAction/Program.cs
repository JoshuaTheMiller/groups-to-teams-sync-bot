using CommandLine;
using GitHubAction.Extensions;
using GitHubAction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static CommandLine.Parser;
using Octokit;
using Azure.Identity;
using Microsoft.Graph;
using Newtonsoft.Json;
using Gttsb.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Microsoft.Extensions.Caching.Memory;

using IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((_, services) => services.AddGitHubActionServices())
    .Build();

static TService Get<TService>(IHost host)
    where TService : notnull =>
    host.Services.GetRequiredService<TService>();

var parser = Default.ParseArguments<ActionInputs>(() => new(), args);
parser.WithNotParsed(
    errors =>
    {
        Get<ILoggerFactory>(host)
            .CreateLogger("GitHubAction.Program")
            .LogError(
                string.Join(
                    Environment.NewLine, errors.Select(error => error.ToString())));

        Environment.Exit(2);
    });

await parser.WithParsedAsync(async options => 
{
	var configurationFromFile = await LoadConfigurationFromFileAsync(options.ConfigPath);

	var renderedInput = new RenderedInput(
		TenantId: options.TenantId,
		ClientId: options.ClientId,		
		GitHubTeamNames: options.GitHubTeamNames.IsEmptyOrContainsOneBlankString() ? configurationFromFile.GitHubTeamNames : options.GitHubTeamNames,
		EmailPrepend: options.EmailPrepend,
		EmailAppend: options.EmailAppend,
		EmailTextToReplace: options.EmailTextToReplace,
		ConfigPath: options.ConfigPath,
		ClientSecret: options.ClientSecret,
		OrgAdministerToken: options.OrgAdministerToken,
		GitHubRepositoryOwner: options.GitHubRepositoryOwner,
		OrganizationMembersGroup: options.OrganizationMembersGroup.IsEmptyOrWhitespace() ? configurationFromFile.OrganizationMembersGroup : options.OrganizationMembersGroup
	);

	await StartTeamSyncAsync(renderedInput, host);
});

static async Task<InputsFromFile> LoadConfigurationFromFileAsync(string configPath)
{
	var text = await System.IO.File.ReadAllTextAsync(configPath) ?? "";

	if (configPath.EndsWith(".json"))
    {		
		return JsonConvert.DeserializeObject<InputsFromFile>(text) ?? new InputsFromFile();
	}

	if(configPath.EndsWith(".yml") || configPath.EndsWith(".yaml"))
    {
		var deserializer = new DeserializerBuilder().Build();

		return deserializer.Deserialize<InputsFromFile>(text) ?? new InputsFromFile();
	}

	return new InputsFromFile();
}

await host.RunAsync();

static async Task StartTeamSyncAsync(RenderedInput inputs, IHost host)
{
    if (!inputs.GitHubTeamNames.Any())
    {
        Console.WriteLine("No teams found to syncronize!");
        Environment.Exit(0);
    }

    // Azure AD Group and GitHub Team Name must match (my opinion, baked into this tool)	
    var groupDisplayNames = inputs.GitHubTeamNames.Concat(new[] { inputs.OrganizationMembersGroup }).Distinct().ToDictionary(t => t);

    var tenantId = inputs.TenantId;
    var clientId = inputs.ClientId;
    var clientSecret = inputs.ClientSecret;

    var tokenAuth = new Credentials(inputs.OrgAdministerToken);

    var org = inputs.GitHubRepositoryOwner;

    var emailPrepend = inputs.EmailPrepend;
    var itemsToReplace = inputs.EmailTextToReplace;
    var emailAppend = inputs.EmailAppend;

    // The client credentials flow requires that you request the
    // /.default scope, and preconfigure your permissions on the
    // app registration in Azure. An administrator must grant consent
    // to those permissions beforehand.
    var scopes = new[] { "https://graph.microsoft.com/.default" };
    // using Azure.Identity;
    var options = new TokenCredentialOptions
    {
        AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
    };
    // https://docs.microsoft.com/dotnet/api/azure.identity.clientsecretcredential
    var clientSecretCredential = new ClientSecretCredential(
        tenantId, clientId, clientSecret, options);
    var graphClient = new GraphServiceClient(clientSecretCredential);
    var activeDirectoryFacade = new ActiveDirectoryFacade(graphClient);

    var client = new GitHubClient(new ProductHeaderValue("groups-to-teams-sync"))
    {
        Credentials = tokenAuth
    };

    var gitHubFacade = new GitHubFacadeCacheDecorator(new GitHubFacade(client), new MemoryCache(new MemoryCacheOptions()));

    var emailToCloudIdBuilder = EmailToCloudIdBuilder.Build(emailPrepend, emailAppend, itemsToReplace);

    var groupSyncer = GroupSyncerBuilder.Build(activeDirectoryFacade, gitHubFacade, emailToCloudIdBuilder);

    var groupsToSyncronize = groupDisplayNames.Select(g => new
    {
        Key = g.Key,
        Value = new TeamDefinition("ActiveDirectory", g.Key)
    }).ToDictionary(o => o.Key, o => o.Value);

    Console.WriteLine("This Action will attempt to syncronize the following groups:");
    foreach (var group in groupsToSyncronize)
    {
        Console.WriteLine($"* {group.Key}");
    }

    var usersWithSyncIssues = new List<GitHubUser>();

    if (!inputs.OrganizationMembersGroup.IsEmptyOrWhitespace())
    {
        var memberSyncResult = await groupSyncer.SyncronizeMembersAsync(org, groupsToSyncronize[inputs.OrganizationMembersGroup]);
        usersWithSyncIssues.AddRange(memberSyncResult.UsersWithSyncIssues);
    }

    var groupSyncResult = await groupSyncer.SyncronizeGroupsAsync(org, groupsToSyncronize.Values);

    usersWithSyncIssues.AddRange(groupSyncResult.UsersWithSyncIssues);

    WriteConsoleOutput(usersWithSyncIssues.DistinctBy(g => g.Email).ToHashSet());

    await Task.CompletedTask;

    Environment.Exit(0);
}

static void WriteConsoleOutput(ISet<GitHubUser> usersWithSyncIssues)
{
    if (usersWithSyncIssues.Any())
    {
        Console.WriteLine("################################################");
        Console.WriteLine();
        Console.WriteLine("There were issues with the following users:");
        Console.WriteLine();
        foreach (var user in usersWithSyncIssues)
        {
            Console.WriteLine($"{user.Email} ==> {user.GitHubId}");
        }
        Console.WriteLine();
        Console.WriteLine("################################################");
    }

    var formattedUsersWithSyncIssues = JsonConvert.SerializeObject(usersWithSyncIssues) ?? "";

    Console.WriteLine("Complete!");

    Console.WriteLine($"::set-output name=users-with-sync-issues::{formattedUsersWithSyncIssues}");
}