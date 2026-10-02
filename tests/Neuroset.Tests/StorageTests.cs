using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neuroset.Application;
using Neuroset.Domain;
using Neuroset.Storage;

namespace Neuroset.Tests;

public sealed class StorageTests : IAsyncLifetime
{
    private const string LegacyConnectionsSchema = """
        CREATE TABLE Connections (
            Id TEXT PRIMARY KEY, Name TEXT NOT NULL, BaseUrl TEXT NOT NULL, ApiKeySecretId TEXT NOT NULL,
            Model TEXT NOT NULL, Organization TEXT NULL, TimeoutSeconds INTEGER NOT NULL DEFAULT 120,
            Temperature REAL NOT NULL DEFAULT 0.7, MaxTokens INTEGER NOT NULL DEFAULT 4096,
            IsActive INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
        """;

    [Fact]
    public async Task Connection_Backup_Key_References_RoundTrip_In_Order_And_Can_Be_Removed()
    {
        var repository = new SqliteConnectionRepository(_database);
        var profile = new ConnectionProfile { Name = "Provider", ApiKeySecretId = "primary-reference", BackupApiKeySecretIds = ["backup-two", "backup-one"] };
        await repository.SaveAsync(profile);
        (await repository.GetAsync(profile.Id)).Should().BeEquivalentTo(profile, options => options.WithStrictOrdering());
        (await repository.ListAsync()).Single().BackupApiKeySecretIds.Should().Equal("backup-two", "backup-one");
        profile.BackupApiKeySecretIds.RemoveAt(0);
        await repository.SaveAsync(profile);
        (await repository.GetAsync(profile.Id))!.BackupApiKeySecretIds.Should().Equal("backup-one");
        profile.BackupApiKeySecretIds.Clear();
        await repository.SaveAsync(profile);
        (await repository.GetAsync(profile.Id))!.BackupApiKeySecretIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Migration_Adds_Optional_Backup_Keys_Without_Changing_Existing_Connections()
    {
        var path = Path.Combine(_directory, "version-five.db");
        var id = Guid.NewGuid();
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = LegacyConnectionsSchema + """
                CREATE TABLE SchemaMigrations (Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL);
                INSERT INTO SchemaMigrations VALUES(5,'2026-01-01');
                INSERT INTO Connections(Id,Name,BaseUrl,ApiKeySecretId,Model,IsActive,CreatedAt,UpdatedAt)
                VALUES($id,'Existing','https://example.test/v1','existing-secret','model',1,'2026-01-01','2026-01-01');
                """;
            command.Parameters.AddWithValue("$id", id.ToString());
            await command.ExecuteNonQueryAsync();
        }
        var database = new SqliteDatabase(new DatabaseOptions(path), NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync();
        await database.InitializeAsync();
        var loaded = (await new SqliteConnectionRepository(database).GetAsync(id))!;
        loaded.Name.Should().Be("Existing");
        loaded.ApiKeySecretId.Should().Be("existing-secret");
        loaded.IsActive.Should().BeTrue();
        loaded.BackupApiKeySecretIds.Should().BeEmpty();
    }

    private readonly string _directory=Path.Combine(Path.GetTempPath(),"NeurosetTests",Guid.NewGuid().ToString("N"));private SqliteDatabase _database=null!;
    public async Task InitializeAsync(){Directory.CreateDirectory(_directory);_database=new SqliteDatabase(new DatabaseOptions(Path.Combine(_directory,"test.db")),NullLogger<SqliteDatabase>.Instance);await _database.InitializeAsync();}
    public Task DisposeAsync(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);return Task.CompletedTask;}
    [Fact]public async Task Migration_Creates_All_Core_Tables(){await using var connection=await _database.OpenAsync();await using var command=connection.CreateCommand();command.CommandText="SELECT name FROM sqlite_master WHERE type='table';";var names=new List<string>();await using var reader=await command.ExecuteReaderAsync();while(await reader.ReadAsync())names.Add(reader.GetString(0));names.Should().Contain(["Conversations","Messages","Attachments","Settings","Themes","Memory","ScheduledTasks","ToolPermissions","KnowledgeSources","KnowledgeChunks","Connections"]);}
    [Fact]public async Task Migration_Upgrades_Existing_Version_Two_Conversation_Data(){var path=Path.Combine(_directory,"version-two.db");await using(var connection=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}")){await connection.OpenAsync();await using var command=connection.CreateCommand();command.CommandText="""CREATE TABLE SchemaMigrations (Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL); INSERT INTO SchemaMigrations VALUES(1,'2026-01-01'),(2,'2026-01-01'); CREATE TABLE Conversations (Id TEXT PRIMARY KEY, Title TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, IsPinned INTEGER NOT NULL DEFAULT 0, IsArchived INTEGER NOT NULL DEFAULT 0, Mode INTEGER NOT NULL DEFAULT 0, WorkspacePath TEXT NULL, FullAccessEnabled INTEGER NOT NULL DEFAULT 0); INSERT INTO Conversations(Id,Title,CreatedAt,UpdatedAt) VALUES('00000000-0000-0000-0000-000000000001','Existing','2026-01-01','2026-01-01');""" + LegacyConnectionsSchema;await command.ExecuteNonQueryAsync();}var database=new SqliteDatabase(new DatabaseOptions(path),NullLogger<SqliteDatabase>.Instance);await database.InitializeAsync();await using var upgraded=await database.OpenAsync();await using var query=upgraded.CreateCommand();query.CommandText="SELECT SystemPrompt FROM Conversations WHERE Title='Existing';";(await query.ExecuteScalarAsync()).Should().Be(string.Empty);}
    [Fact]public async Task Migration_Preserves_Legacy_Chat_System_Prompt_As_Custom_Profile(){var path=Path.Combine(_directory,"version-three.db");await using(var connection=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}")){await connection.OpenAsync();await using var command=connection.CreateCommand();command.CommandText="""CREATE TABLE SchemaMigrations (Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL); INSERT INTO SchemaMigrations VALUES(1,'2026-01-01'),(2,'2026-01-01'),(3,'2026-01-01'); CREATE TABLE Conversations (Id TEXT PRIMARY KEY, Title TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, IsPinned INTEGER NOT NULL DEFAULT 0, IsArchived INTEGER NOT NULL DEFAULT 0, Mode INTEGER NOT NULL DEFAULT 0, WorkspacePath TEXT NULL, FullAccessEnabled INTEGER NOT NULL DEFAULT 0, SystemPrompt TEXT NOT NULL DEFAULT ''); INSERT INTO Conversations(Id,Title,CreatedAt,UpdatedAt,SystemPrompt) VALUES('00000000-0000-0000-0000-000000000002','Legacy','2026-01-01','2026-01-01','Keep this prompt');""" + LegacyConnectionsSchema;await command.ExecuteNonQueryAsync();}var database=new SqliteDatabase(new DatabaseOptions(path),NullLogger<SqliteDatabase>.Instance);await database.InitializeAsync();var loaded=await new SqliteConversationRepository(database).GetAsync(Guid.Parse("00000000-0000-0000-0000-000000000002"));loaded.Should().NotBeNull();loaded!.UseCustomGenerationSettings.Should().BeTrue();loaded.Generation.SystemPrompt.Should().Be("Keep this prompt");}
    [Fact]public async Task Conversation_And_Message_RoundTrip(){var repository=new SqliteConversationRepository(_database);var conversation=new Conversation{Title="Test"};await repository.SaveAsync(conversation);var message=new ChatMessage{ConversationId=conversation.Id,Role=MessageRole.User,Content="hello",Metadata=new Dictionary<string,string>{{"source","test"}}};await repository.SaveMessageAsync(message);(await repository.GetAsync(conversation.Id)).Should().BeEquivalentTo(conversation);var loaded=(await repository.GetMessagesAsync(conversation.Id)).Single();loaded.Content.Should().Be("hello");loaded.Metadata["source"].Should().Be("test");}
    [Fact]public async Task Coding_Conversation_Persists_Workspace_And_Custom_Generation_Profile(){var repository=new SqliteConversationRepository(_database);var conversation=new Conversation{Title="Code",Mode=ConversationMode.Coding,WorkspacePath=_directory,FullAccessEnabled=true,UseCustomGenerationSettings=true,Generation=new GenerationSettings{SystemPrompt="Use the repository conventions.",Temperature=1.1,TopP=.8,MaxTokens=1234,PresencePenalty=.2,FrequencyPenalty=-.1,Seed=9,StopSequences=["DONE"],EnableTools=false}};await repository.SaveAsync(conversation);var loaded=await repository.GetAsync(conversation.Id);loaded.Should().BeEquivalentTo(conversation);}
    [Fact]public async Task Roleplay_Conversation_Persists_Characters_Scene_Player_Pause_And_Turn_Order(){var repository=new SqliteConversationRepository(_database);var character=new RoleplayCharacter{Name="Mira",SystemPrompt="Speak as a curious astronomer."};var conversation=new Conversation{Title="Mira",Mode=ConversationMode.Roleplay,Generation=new GenerationSettings{EnableTools=false},Roleplay=new RoleplaySettings{Characters=[character],SceneDescription="A storm-locked lighthouse.",PlayerName="Elias",PlayerDescription="A tired radio operator.",IsPaused=true,StartingSpeakerId=character.Id.ToString("D"),PendingSpeakerId=character.Id.ToString("D")}};await repository.SaveAsync(conversation);var loaded=await repository.GetAsync(conversation.Id);loaded.Should().BeEquivalentTo(conversation);loaded!.Roleplay.ResolvedPlayerName.Should().Be("Elias");}
    [Fact]public async Task Roleplay_Conversation_Without_A_Player_Name_Falls_Back_To_The_Default(){var repository=new SqliteConversationRepository(_database);var conversation=new Conversation{Title="Mira",Mode=ConversationMode.Roleplay,Roleplay=new RoleplaySettings{Characters=[new RoleplayCharacter{Name="Mira",SystemPrompt="Speak as a curious astronomer."}]}};await repository.SaveAsync(conversation);var loaded=await repository.GetAsync(conversation.Id);loaded!.Roleplay.SceneDescription.Should().BeEmpty();loaded.Roleplay.ResolvedPlayerName.Should().Be("User");}
    [Fact]public async Task Message_Edit_Creates_Navigable_Branch_Without_Deleting_Original(){var repository=new SqliteConversationRepository(_database);var conversation=new Conversation{Title="Branches"};await repository.SaveAsync(conversation);var user=new ChatMessage{ConversationId=conversation.Id,Role=MessageRole.User,Content="first"};await repository.SaveMessageAsync(user);var assistant=new ChatMessage{ConversationId=conversation.Id,Role=MessageRole.Assistant,Content="old answer",ParentMessageId=user.Id};await repository.SaveMessageAsync(assistant);var edited=await repository.CreateMessageVersionAsync(user.Id,"edited");var editedPath=await repository.GetMessagesAsync(conversation.Id);editedPath.Should().ContainSingle().Which.Content.Should().Be("edited");editedPath[0].VersionCount.Should().Be(2);(await repository.ActivateAdjacentMessageVersionAsync(edited.Id,-1)).Should().BeTrue();var originalPath=await repository.GetMessagesAsync(conversation.Id);originalPath.Select(x=>x.Content).Should().Equal("first","old answer");var assistantVersion=await repository.CreateMessageVersionAsync(assistant.Id,"new answer");(await repository.GetMessagesAsync(conversation.Id)).Select(x=>x.Content).Should().Equal("first","new answer");await repository.DeleteMessageAsync(assistantVersion.Id);(await repository.GetMessagesAsync(conversation.Id)).Select(x=>x.Content).Should().Equal("first","old answer");}
    [Fact]public async Task Settings_RoundTrip(){var repository=new SqliteSettingsRepository(_database);var settings=new AppSettings{FirstRunCompleted=true,InterfaceLanguage="en-US",FullAutonomyEnabled=true,Appearance=new AppearanceSettings{Theme="Nord"},Generation=new GenerationSettings{SystemPrompt="Global",Temperature=.25,TopP=.9,Seed=77}};await repository.SaveAsync(settings);var loaded=await repository.LoadAsync();loaded.FirstRunCompleted.Should().BeTrue();loaded.InterfaceLanguage.Should().Be("en-US");loaded.FullAutonomyEnabled.Should().BeTrue();loaded.Appearance.Theme.Should().Be("Nord");loaded.Generation.Should().BeEquivalentTo(settings.Generation);}
    [Fact]public async Task Memory_Can_Be_Viewed_Deleted_And_Cleared(){var store=new SqliteMemoryStore(_database);var first=new MemoryRecord{Scope=MemoryScope.LongTerm,Content="prefers concise answers"};var second=new MemoryRecord{Scope=MemoryScope.ShortTerm,ConversationId=Guid.NewGuid(),Content="current task"};await store.SaveAsync(first);await store.SaveAsync(second);(await store.ListAsync()).Should().HaveCount(2);await store.DeleteAsync(first.Id);(await store.ListAsync()).Should().ContainSingle();await store.ClearAsync();(await store.ListAsync()).Should().BeEmpty();}

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Legacy_Interrupted_Responses_Recover_Only_Their_Own_Saved_Tool_Phases_Without_Rewriting_The_Database(bool interrupted)
    {
        var repository = new SqliteConversationRepository(_database);
        var conversation = new Conversation { Title = "Interrupted research" };
        await repository.SaveAsync(conversation);
        var start = DateTimeOffset.UtcNow;
        var user = new ChatMessage { ConversationId = conversation.Id, Role = MessageRole.User, Content = "Create a presentation.", CreatedAt = start };
        var aggregate = new ChatMessage
        {
            ConversationId = conversation.Id, Role = MessageRole.Assistant, ParentMessageId = user.Id,
            Content = "Partial answer", Status = MessageStatus.Failed, CreatedAt = start.AddSeconds(1), UpdatedAt = start.AddSeconds(20),
            Metadata = new() { [AgentRunMessageMetadata.StartedAt] = "original-run" },
            ToolCalls = [new() { Id = "saved-1", Name = "echo" }, new() { Id = "pending", Name = "echo" }]
        };
        if (interrupted) aggregate.Metadata["interrupted"] = "true";
        var first = Phase(user.Id, "saved-1", "original-run", 2);
        var second = Phase(first.Id, "saved-2", "original-run", 5);
        var otherRun = Phase(user.Id, "other-run-result", "different-run", 3);
        var inactive = Phase(user.Id, "inactive-result", "original-run", 4);
        inactive.IsActiveVersion = false;
        var continuation = new ChatMessage { ConversationId = conversation.Id, Role = MessageRole.User, ParentMessageId = aggregate.Id, Content = "Continue.", CreatedAt = start.AddSeconds(25) };
        foreach (var message in new[] { user, aggregate, first, second, otherRun, inactive, continuation }) await repository.SaveMessageAsync(message);

        var loaded = await repository.GetMessagesAsync(conversation.Id);
        loaded.Select(message => message.Id).Should().Equal(user.Id, aggregate.Id, continuation.Id);
        var restored = loaded[1];
        restored.Content.Should().Be("Partial answer");
        restored.Status.Should().Be(MessageStatus.Failed);
        restored.ToolCalls.Select(call => call.Id).Should().Equal(interrupted ? ["saved-1", "saved-2", "pending"] : ["saved-1", "pending"]);
        restored.Metadata.ContainsKey(LlmToolHistory.RecoveredCallsMetadataKey).Should().Be(interrupted);
        if (interrupted) restored.ToolCalls.First().Execution!.Output.Should().Be("Recorded saved-1");
        (await repository.GetMessagesAsync(conversation.Id))[1].ToolCalls.Should().BeEquivalentTo(restored.ToolCalls);

        var stored = (await repository.GetMessageAsync(aggregate.Id))!;
        stored.ToolCalls.Should().HaveCount(2);
        stored.ToolCalls.First().Execution.Should().BeNull();
        stored.Metadata.Should().NotContainKey(LlmToolHistory.RecoveredCallsMetadataKey);
        (await repository.GetMessageAsync(first.Id))!.ToolCalls.Single().Execution!.Output.Should().Be("Recorded saved-1");

        ChatMessage Phase(Guid parent, string id, string run, int seconds) => new()
        {
            ConversationId = conversation.Id, Role = MessageRole.Assistant, ParentMessageId = parent, CreatedAt = start.AddSeconds(seconds),
            Metadata = new() { [AgentRunMessageMetadata.StartedAt] = run },
            ToolCalls = [new() { Id = id, Name = "echo", ArgumentsJson = "{}", Execution = new() { ToolCallId = id, ToolName = "echo", Succeeded = true, Output = "Recorded " + id } }]
        };
    }
}
