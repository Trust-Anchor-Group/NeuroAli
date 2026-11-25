using NeuroAli;
using Waher.Mcp.Identity.Resources;
using Waher.Networking.HTTP.JsonRpc.MetaData;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Waher.Content.Html;
using Waher.Events;
using Waher.Events.Files;
using Waher.Events.Persistence;
using Waher.Mcp.Content;
using Waher.Mcp.Events;
using Waher.Mcp.Files;
using Waher.Mcp.Identity;
using Waher.Mcp.Identity.Responses;
using Waher.Mcp.Payments;
using Waher.Mcp.Xmpp;
using Waher.Mcp.Xmpp.Responses;
using Waher.Networking;
using Waher.Networking.HTTP;
using Waher.Networking.HTTP.JsonRpc;
using Waher.Networking.HTTP.JsonRpc.Transports;
using Waher.Networking.HTTP.Mcp;
using Waher.Networking.HTTP.Mcp.Model;
using Waher.Networking.HTTP.Mcp.Model.Attributes;
using Waher.Networking.HTTP.Mcp.Model.Server;
using Waher.Networking.Sniffers;
using Waher.Persistence;
using Waher.Persistence.Files;
using Waher.Runtime.Console;
using Waher.Runtime.Inventory;
using Waher.Runtime.Inventory.Loader;
using Waher.Security.JWT;
using Waher.Networking.XMPP.Contracts;
using Waher.Networking.XMPP.Contracts.EventArguments;

internal class Program
{
	private sealed class JoinedStdioMcpServer : StdioMcpServer
	{
		private static readonly System.Reflection.MethodInfo GenerateInputFormMethod = typeof(HttpMcpServerResource).GetMethod(
			"GenerateInputForm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
		private readonly HttpMcpServerResource[] McpServers;

		public JoinedStdioMcpServer(HttpMcpServerResource[] Servers, string ResourceName,
			string Name, string Description, Icon[] Icons, Uri? WebsiteUrl, ISnifferSet Sniffers)
			: base(Servers, ResourceName, Name, Description, Icons, WebsiteUrl, Sniffers)
		{
			this.McpServers = Servers;
		}

		public override async Task<Waher.Networking.HTTP.Mcp.Model.Server.Resource?> TryGetResource(
			IJsonRpcCall Call, Waher.Security.IUser? User, Uri Uri,
			Waher.Networking.HTTP.Mcp.Model.Server.Session? Session)
		{
			foreach (HttpMcpServerResource Server in this.McpServers)
			{
				var Resource = await Server.TryGetResource(Call, User, Uri, Session);
				if (Resource is not null) return Resource;
				foreach (var Listed in await Server.GetResources(Call, User, Session))
					if (Listed.Uri == Uri) return Listed;
			}
			return null;
		}

		[JsonRpcMethod]
		[JsonRpcDocName("resources/read")]
		protected override async Task<Dictionary<string, object>?> Resources_Read(IJsonRpcCall Call, Uri Uri,
			[JsonRpcMetaDataArgument] object? _Meta = null)
		{
			// Preserve default resource output; JSON is explicitly negotiated by clients.
			var Result = await base.Resources_Read(Call, Uri, _Meta);
			if (Result is null || !Result.ContainsKey("contents") ||
				_Meta is not Dictionary<string, object> Meta ||
				!Meta.TryGetValue("reliefmesh/format", out var Format) || !Equals(Format, "json")) return Result;
			var Session = await this.TryGetMcpSession(Call);
			var Resource = await this.TryGetResource(Call, Session?.User, Uri, Session);
			object? Value = Resource switch {
				IdentityResource Identity => Identity.Identity.ToJson(),
				ContractResource Contract => Contract.Contract.ToJson(),
				_ => null
			};
			if (Value is not null) Result["contents"] = new[] { new Dictionary<string, object> {
				["uri"] = Uri.OriginalString, ["mimeType"] = "application/json", ["text"] = Waher.Content.JSON.Encode(Value, false)
			} };
			return Result;
		}

		public override async Task GET(HttpRequest Request, HttpResponse Response)
		{
			string SubPath = Request.SubPath;
			if (!string.IsNullOrEmpty(SubPath) && SubPath.Length > 1)
			{
				string ElicitationId = SubPath[1..];
				if (!ElicitationId.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
				{
					if (!this.TryGetRequest(ElicitationId, out IJsonRpcClientRequest? RegisteredRequest) || RegisteredRequest.Tag is null)
					{
						foreach (HttpMcpServerResource Server in this.McpServers)
						{
							if (Server.TryGetRequest(ElicitationId, out IJsonRpcClientRequest? SourceRequest) &&
								SourceRequest.Tag is not null)
							{
								Task<HtmlDocument> GenerateInputForm = (Task<HtmlDocument>)GenerateInputFormMethod.Invoke(
									Server, [Request, Response, SourceRequest])!;
								await Response.Return(await GenerateInputForm);
								return;
							}
						}
					}
				}
			}

			await base.GET(Request, Response);
		}

		public override async Task POST(HttpRequest Request, HttpResponse Response)
		{
			string SubPath = Request.SubPath;
			if (!string.IsNullOrEmpty(SubPath) && SubPath.Length > 1)
			{
				string ElicitationId = SubPath[1..];
				foreach (HttpMcpServerResource Server in this.McpServers)
				{
					if (Server.TryGetRequest(ElicitationId, out IJsonRpcClientRequest? SourceRequest) &&
						SourceRequest.Tag is not null)
					{
						await Server.POST(Request, Response);
						return;
					}
				}
			}

			await base.POST(Request, Response);
		}
	}

	private sealed class CompatibleIdentityMcpServer : IdentityMcpServer
	{
		private sealed class ContractProposalDecision
		{
			[McpParameter("Accept", "If you accept the proposal and want to sign the contract.")]
			public bool? Accept = null;
		}

		private static readonly System.Reflection.FieldInfo? ContractProposalReceivedField =
			typeof(ContractsClient).GetField("ContractProposalReceived",
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		private readonly HashSet<ContractsClient> ProposalClients = new();

		public CompatibleIdentityMcpServer(string ResourceName, XmppMcpServer XmppMcpServer, ISnifferSet SnifferSet)
			: base(ResourceName, XmppMcpServer, SnifferSet)
		{
		}

		private static void ProposalTrace(string Marker, string? Result = null)
		{
			ConsoleError.WriteLine(Result is null ? Marker : Marker + " result=" + Result);
		}

		private void EnsureCompatibleProposalHandler(ContractsClient Client)
		{
			lock (this.ProposalClients)
			{
				if (!this.ProposalClients.Add(Client))
					return;
			}

			if (ContractProposalReceivedField?.GetValue(Client) is Delegate Handlers)
			{
				foreach (Delegate Handler in Handlers.GetInvocationList())
				{
					if (ReferenceEquals(Handler.Target, this) &&
						Handler.Method.Name == "ContractsClient_ContractProposalReceived")
					{
						Client.ContractProposalReceived -=
							(EventHandlerAsync<ContractProposalEventArgs>)Handler;
					}
				}
			}

			Client.ContractProposalReceived += this.ContractsClient_CompatibleContractProposalReceived;
			ProposalTrace("PROPOSAL_TRACE_HANDLER_INSTALLED");
		}

		private async Task<Contract?> TryReadProposedContract(Waher.Security.IUser User,
			ContractsClient Client, string ContractId)
		{
			ProposalTrace("PROPOSAL_TRACE_CONTRACT_READ_START");

			try
			{
				Contract? Contract = await PetitionCache.TryGetContract(User.UserName,
					ContractId, Client)
					?? await Client.GetContractAsync(ContractId);

				ProposalTrace("PROPOSAL_TRACE_CONTRACT_READ_RESULT", "SUCCESS");
				return Contract;
			}
			catch (Exception ex)
			{
				string Result = ex.Message.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
					? "NOT_AUTHORIZED"
					: "FAILED";
				ProposalTrace("PROPOSAL_TRACE_CONTRACT_READ_RESULT", Result);
				return null;
			}
		}

		private async Task<string> HandleContractProposal(IJsonRpcCall Call,
			Waher.Security.IUser User, Session Session, ContractsClient Client,
			string ContractId, string Role, string FromBareJid, string Message,
			bool SignAfterApproval)
		{
			Contract? Contract = await this.TryReadProposedContract(User, Client, ContractId);
			if (Contract is null)
				return "Contract access is not authorized; no approval was requested.";

			await PetitionCache.AddContract(User.UserName, Contract);
			this.ResourcesUpdated(User);

			StringBuilder Text = new();
			Text.Append("You have received a contract proposal from ");
			Text.Append(FromBareJid);
			Text.Append(": ");
			Text.AppendLine(Message);
			Text.Append("Contract: ");
			Text.AppendLine(ContractId);
			Text.AppendLine();
			Text.AppendLine("Contents of the contract proposal follows:");
			Text.AppendLine();
			Text.Append(await Contract.ToPlainText(Contract.DefaultLanguage));

			ProposalTrace("PROPOSAL_TRACE_ELICITATION_SEND");
			bool? Result = await this.ElicitUserInput(Call, Text.ToString(), new ContractProposalDecision(),
				true, Session, 15 * 60 * 1000);
			ProposalTrace("PROPOSAL_TRACE_ELICITATION_RESULT",
				Result.HasValue ? (Result.Value ? "ACCEPTED" : "DECLINED") : "NO_RESPONSE");

			if (!Result.HasValue)
				return "No approval response was received; the contract was not signed.";
			if (!Result.Value)
				return "The proposal was declined; the contract was not signed.";
			if (!SignAfterApproval)
				return "The proposal was approved for signing by the MCP caller.";

			Contract = await Client.SignContractAsync(Contract, Role, false);
			await PetitionCache.AddContract(User.UserName, Contract);
			this.ResourcesUpdated(User);
			return "The proposal was accepted and the smart contract was signed.";
		}

		private async Task ContractsClient_CompatibleContractProposalReceived(object Sender,
			ContractProposalEventArgs e)
		{
			ProposalTrace("PROPOSAL_TRACE_RECEIVED");

			if (Sender is not ContractsClient Client ||
				!Client.Client.TryGetTag("User", out Waher.Security.IUser? User) || User is null ||
				!Client.Client.TryGetExtension(out McpXmppExtension McpXmppExtension))
			{
				ProposalTrace("PROPOSAL_TRACE_MCP_SESSION_FOUND", "NO");
				return;
			}

			foreach (string SessionId in McpXmppExtension.SessionIds)
			{
				if (this.TryGetMcpSession(SessionId, out Session? Session) && Session.User is not null)
				{
					ProposalTrace("PROPOSAL_TRACE_MCP_SESSION_FOUND", "YES");
					await this.HandleContractProposal(McpXmppExtension.FirstCall, User, Session,
						Client, e.ContractId, e.Role, e.FromBareJID, e.MessageText, true);
					return;
				}
			}

			ProposalTrace("PROPOSAL_TRACE_MCP_SESSION_FOUND", "NO");
		}

		[McpServerTool(
			"Recover Contract Proposal",
			"Locally resumes a previously received contract proposal without sending it again.",
			"",
			true,
			false,
			false,
			true)]
		public async Task<GenericResponse> RecoverContractProposal(
			IJsonRpcCall Call,
			[McpStringParameter("ContractId", "Identifier of the previously proposed smart contract.")]
			string ContractId,
			[McpStringParameter("Role", "Role proposed to the recipient.")]
			string Role,
			[McpStringParameter("From", "Bare JID recorded on the received proposal.")]
			string From,
			[McpStringParameter("Message", "Message recorded on the received proposal.")]
			string Message)
		{
			Session? Session = await this.TryGetMcpSession(Call);
			if (Session is null)
				return new GenericResponse(false, "No MCP session.");

			Waher.Security.IUser? User = await this.GetAuthenticatedUser(Call, Session);
			if (Call.ResponseSent || User is null)
				return new GenericResponse(false, "User not authenticated.");

			ContractsClient? Client = await this.GetClient(Call, User, Session, true);
			if (Client is null)
				return new GenericResponse(false, "MCP XMPP Contracts client not available.");

			this.EnsureCompatibleProposalHandler(Client);
			ProposalTrace("PROPOSAL_TRACE_RECOVERY_START");
			string Result = await this.HandleContractProposal(Call, User, Session, Client,
				ContractId, Role, From, Message, false);
			return new GenericResponse(!Result.StartsWith("Contract access", StringComparison.Ordinal), Result);
		}

		[McpServerTool(
			"Authorize Contract Access",
			"Authorizes a specific legal identity to read a smart contract.",
			"",
			true,
			false,
			true,
			true)]
		public async Task<GenericResponse> AuthorizeContractAccess(
			IJsonRpcCall Call,
			[McpStringParameter("ContractId", "Identifier of the smart contract.")]
			string ContractId,
			[McpStringParameter("RemoteLegalId", "Legal identity receiving contract access.")]
			string RemoteLegalId)
		{
			Session? Session = await this.TryGetMcpSession(Call);
			if (Session is null)
				return new GenericResponse(false, "No MCP session.");

			Waher.Security.IUser? User = await this.GetAuthenticatedUser(Call, Session);
			if (Call.ResponseSent || User is null)
				return new GenericResponse(false, "User not authenticated.");

			ContractsClient? Client = await this.GetClient(Call, User, Session, true);
			if (Client is null)
				return new GenericResponse(false, "MCP XMPP Contracts client not available.");

			try
			{
				await Client.AuthorizeAccessToContractAsync(ContractId, RemoteLegalId, true);
				ProposalTrace("PROPOSAL_TRACE_ACCESS_GRANT_RESULT", "SUCCESS");
				return new GenericResponse(true, "Contract access authorized.");
			}
			catch (Exception)
			{
				ProposalTrace("PROPOSAL_TRACE_ACCESS_GRANT_RESULT", "FAILED");
				return new GenericResponse(false, "Contract access authorization failed.");
			}
		}

		public override async Task<Waher.Networking.HTTP.Mcp.Model.Server.Resource[]> GetResources(
			IJsonRpcCall Call, Waher.Security.IUser? User,
			Waher.Networking.HTTP.Mcp.Model.Server.Session? Session)
		{
			var Resources = (await base.GetResources(Call, User, Session)).ToList();
			if (User is null || Session is null)
				return Resources.ToArray();

			ContractsClient? Client = await this.GetClient(Call, User, Session, false);
			if (Client is null)
				return Resources.ToArray();

			this.EnsureCompatibleProposalHandler(Client);

			HashSet<string> Uris = new(Resources.Select(Resource => Resource.Uri.OriginalString));
			foreach (Contract Contract in (await Client.GetCreatedContractsAsync()).Contracts)
			{
				if (Uris.Add(Contract.ContractIdUriString))
					Resources.Add(new ContractResource(Contract));
			}

			return Resources.ToArray();
		}

		public override async Task<Waher.Networking.HTTP.Mcp.Model.Server.Resource?> TryGetResource(
			IJsonRpcCall Call, Waher.Security.IUser? User, Uri Uri,
			Waher.Networking.HTTP.Mcp.Model.Server.Session? Session)
		{
			if (Uri.Scheme != "iotsc" || User is null || Session is null)
				return await base.TryGetResource(Call, User, Uri, Session);

			ContractsClient? Client = await this.GetClient(Call, User, Session, true);
			if (Client is null)
				return null;

			this.EnsureCompatibleProposalHandler(Client);

			Contract Contract = await Client.GetContractAsync(Uri.AbsolutePath);
			await PetitionCache.AddContract(User.UserName, Contract);
			return new ContractResource(Contract);
		}
	}

	private static bool IsInitialToolsListRequest(string Input)
	{
		try
		{
			using JsonDocument Document = JsonDocument.Parse(Input);
			JsonElement Root = Document.RootElement;

			if (!Root.TryGetProperty("method", out JsonElement Method) ||
				Method.ValueKind != JsonValueKind.String || Method.GetString() != "tools/list")
			{
				return false;
			}

			return !Root.TryGetProperty("params", out JsonElement Parameters) ||
				Parameters.ValueKind != JsonValueKind.Object ||
				!Parameters.TryGetProperty("cursor", out JsonElement Cursor) ||
				Cursor.ValueKind == JsonValueKind.Null;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static async Task<string?> TryExecuteCompatibleCreateOpenContract(InternalJsonRpcCall Call,
		IdentityMcpServer Server, string Input)
	{
		const string TemplateId = "323064dd-0e43-4f7b-6833-3d05e10bb267@legal.lab.tagroot.io";
		JsonObject? Root;
		JsonObject? Arguments;

		try
		{
			Root = JsonNode.Parse(Input)?.AsObject();
			if (Root?["method"]?.GetValue<string>() != "tools/call" ||
				Root["params"]?["name"]?.GetValue<string>() != "CreateOpenContract" ||
				(Root["params"]?["arguments"] as JsonObject)?["TemplateId"]?.GetValue<string>() != TemplateId)
			{
				return null;
			}

			Arguments = Root["params"]!["arguments"]!.AsObject();
		}
		catch
		{
			return null;
		}

		JsonObject ParameterNodes = Arguments["Parameters"]!.AsObject();
		Dictionary<string, object> Parameters = new(StringComparer.Ordinal);
		foreach ((string Name, JsonNode? Node) in ParameterNodes)
		{
			if (Name == "Deadline" && Node is JsonValue DateValue &&
				DateValue.TryGetValue(out string? DateText) &&
				DateTime.TryParse(DateText, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime Deadline))
			{
				Parameters[Name] = Deadline;
			}
			else if (Node is JsonValue Value && Value.TryGetValue(out string? StringValue))
				Parameters[Name] = StringValue;
			else if (Node is JsonValue NumberValue && NumberValue.TryGetValue(out decimal DecimalValue))
				Parameters[Name] = DecimalValue;
			else
				throw new JsonException("Unsupported contract parameter value.");
		}

		ContractResponse Response = await Server.CreateOpenContract(Call, TemplateId, Parameters,
			Enum.Parse<ContractVisibility>(Arguments["Visibility"]!.GetValue<string>()),
			Arguments["DurationDays"]!.GetValue<int>(),
			Arguments["ArchiveRequiredDays"]?.GetValue<int>() ?? 365,
			Arguments["ArchiveOptionalDays"]?.GetValue<int>() ?? 0);
		JsonObject Properties = new()
		{
			["Contract"] = JsonSerializer.SerializeToNode(Response.Contract),
			["Success"] = Response.Success,
			["Message"] = Response.Message
		};
		JsonObject ResultValue = new() { ["type"] = "object", ["properties"] = Properties };
		return new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"] = Root["id"]?.DeepClone(),
			["result"] = new JsonObject
			{
				["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = ResultValue.ToJsonString() }),
				["structuredContent"] = new JsonObject { ["result"] = ResultValue.DeepClone() }
			}
		}.ToJsonString();
	}

	private static Task IgnoreInternalEvent(object _, NotificationEventArgs e)
	{
		return Task.CompletedTask;
	}

	private static async Task<string> ExecuteCompleteJoinedToolsList(InternalJsonRpcCall JsonRpcCall,
		StdioMcpServer Mcp, string Input, CommunicationLayer JsonRpcLayer, string BaseUrl)
	{
		string Output = await Mcp.ExecuteJsonRpc(JsonRpcCall, Input, JsonRpcLayer);
		if (!IsInitialToolsListRequest(Input))
			return Output;

		try
		{
			JsonObject? Response = JsonNode.Parse(Output)?.AsObject();
			JsonObject? Result = Response?["result"]?.AsObject();
			JsonArray? Tools = Result?["tools"]?.AsArray();
			string? Cursor = Result?["nextCursor"]?.GetValue<string>();

			if (Response is null || Result is null || Tools is null || string.IsNullOrEmpty(Cursor))
				return Output;

			HashSet<string> SeenCursors = new(StringComparer.Ordinal);
			int PageNumber = 2;
			while (!string.IsNullOrEmpty(Cursor) && SeenCursors.Add(Cursor))
			{
				JsonObject PageRequest = new()
				{
					["jsonrpc"] = "2.0",
					["id"] = "neuroali-compat-tools-page-" + PageNumber++.ToString(),
					["method"] = "tools/list",
					["params"] = new JsonObject { ["cursor"] = Cursor }
				};

				InternalJsonRpcCall PageCall = new(JsonRpcLayer, new StdioUser(), BaseUrl,
					IgnoreInternalEvent, "STDIO");
				if (JsonRpcCall.TryGetSessionId(out string? SessionId) && !string.IsNullOrEmpty(SessionId))
					PageCall.SetSessionId(SessionId);

				if (await Mcp.TryGetMcpSession(PageCall) is { } ExistingSession)
					Mcp.RegisterSession(PageCall, ExistingSession);

				string PageOutput = await Mcp.ExecuteJsonRpc(PageCall,
					PageRequest.ToJsonString(), JsonRpcLayer);
				JsonObject? PageResponse = JsonNode.Parse(PageOutput)?.AsObject();
				JsonObject? PageResult = PageResponse?["result"]?.AsObject();
				JsonArray? PageTools = PageResult?["tools"]?.AsArray();
				string? PageNextCursor = PageResult?["nextCursor"]?.GetValue<string>();

				if (PageResult is null || PageTools is null)
					return Output;

				foreach (JsonNode? Tool in PageTools)
					Tools.Add(Tool?.DeepClone());

				Cursor = PageNextCursor;
			}

			if (!string.IsNullOrEmpty(Cursor))
				return Output;

			Result.Remove("nextCursor");
			return Response.ToJsonString();
		}
		catch
		{
			return Output;
		}
	}

	private static HttpMcpServerResource? GetRequestOwner(string Input,
		Dictionary<string, HttpMcpServerResource> ToolOwners,
		Dictionary<string, HttpMcpServerResource> PromptOwners)
	{
		try
		{
			using JsonDocument Document = JsonDocument.Parse(Input);
			JsonElement Root = Document.RootElement;

			if (!Root.TryGetProperty("method", out JsonElement MethodElement) ||
				MethodElement.ValueKind != JsonValueKind.String ||
				!Root.TryGetProperty("params", out JsonElement Parameters) ||
				Parameters.ValueKind != JsonValueKind.Object ||
				!Parameters.TryGetProperty("name", out JsonElement NameElement) ||
				NameElement.ValueKind != JsonValueKind.String)
			{
				return null;
			}

			string? Name = NameElement.GetString();
			if (string.IsNullOrEmpty(Name))
				return null;

			return MethodElement.GetString() switch
			{
				"tools/call" when ToolOwners.TryGetValue(Name, out HttpMcpServerResource? Owner) => Owner,
				"prompts/get" when PromptOwners.TryGetValue(Name, out HttpMcpServerResource? Owner) => Owner,
				_ => null
			};
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string GetUniqueName(Dictionary<string, HttpMcpServerResource> Owners,
		string Name)
	{
		string Suffix = string.Empty;
		int i = 1;

		while (Owners.ContainsKey(Name + Suffix))
		{
			i++;
			Suffix = "_" + i.ToString();
		}

		return Name + Suffix;
	}

	private static async Task Main(string[] Arguments)
	{
		try
		{
			// Parsing command-line parameters

			string WorkingFolder = Directory.GetCurrentDirectory();
			string ExecutableFolder = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? WorkingFolder;
			string s;
			int HttpPort = 8080;
			bool WorkingFolderFromArgument = false;
			bool SkipWorkingFolderCheck = false;
			List<(string Alias, string Url)> RemoteDefinitions = [];
			Dictionary<string, string> RemoteIssuers = new(StringComparer.OrdinalIgnoreCase);
			int i = 0;
			int c = Arguments.Length;

			while (i < c)
			{
				s = Arguments[i++];

				switch (s)
				{
					case "-d":
						if (i < c)
						{
							WorkingFolder = Arguments[i++];
							WorkingFolderFromArgument = true;
							if (!Directory.Exists(WorkingFolder))
								Directory.CreateDirectory(WorkingFolder);
						}
						else
							throw new Exception("Missing data folder.");
						break;

					case "-p":
						if (i < c)
						{
							s = Arguments[i++];
							if (!int.TryParse(s, out int Port) || Port <= ushort.MinValue || Port > ushort.MaxValue)
								throw new Exception("Invalid port number: " + s);

							HttpPort = Port;
						}
						else
							throw new Exception("Missing port number.");
						break;

					case "-y":
						SkipWorkingFolderCheck = true;
						break;

					case "-r":
						if (i + 1 < c)
						{
							string Alias = Arguments[i++];
							RemoteDefinitions.Add((Alias, Arguments[i++]));
						}
						else
							throw new Exception("Missing alias or URL for -r.");
						break;

					case "-ri":
						if (i + 1 < c)
						{
							string Alias = Arguments[i++];
							RemoteIssuers[Alias] = Arguments[i++];
						}
						else
							throw new Exception("Missing alias or issuer for -ri.");
						break;

					case "-h":
						Console.Out.WriteLine("Command-line parameters:");
						Console.Out.WriteLine();
						Console.Out.WriteLine("-d FOLDER     Sets the working folder where data is stored to FOLDER.");
						Console.Out.WriteLine("-p PORT       Defines the HTTP port number to use as PORT.");
						Console.Out.WriteLine("-y            Uses the working folder even if it contains unrelated files.");
						Console.Out.WriteLine("-r ALIAS URL  Proxies the remote MCP endpoint at URL. Remote tools and prompts");
						Console.Out.WriteLine("              are named ALIAS_NAME. Can be repeated.");
						Console.Out.WriteLine("-ri ALIAS JID Requests JWT tokens for remote ALIAS from JID, instead of the");
						Console.Out.WriteLine("              host name of its URL.");
						Console.Out.WriteLine();
						Console.Out.WriteLine("Remote servers can also be added on http://localhost:PORT/Admin/Remotes.md");
						Console.Out.WriteLine("(local access only). Added servers are used after a restart.");
						Console.Out.WriteLine();
						Console.Out.WriteLine("-h            Shows command-line parameters available.");
						return;
				}
			}

			List<RemoteMcpEndpoint> RemoteEndpoints = [];

			foreach ((string Alias, string Url) in RemoteDefinitions)
			{
				RemoteIssuers.TryGetValue(Alias, out string? Issuer);
				RemoteEndpoints.Add(RemoteMcpEndpoint.Create(Alias, Url, Issuer));
			}

			foreach (string Alias in RemoteIssuers.Keys)
			{
				if (!RemoteDefinitions.Exists(P => string.Equals(P.Alias, Alias, StringComparison.OrdinalIgnoreCase)))
					throw new Exception("-ri refers to unknown remote alias: " + Alias);
			}

			// Checking the working folder before anything is created in it

			if (!SkipWorkingFolderCheck &&
				!WorkingFolderCheck.Confirm(WorkingFolder, WorkingFolderFromArgument, ExecutableFolder))
			{
				Environment.ExitCode = 1;
				return;
			}

			// Initializing type inventory

			TypesLoader.Initialize();
			Types.SetModuleParameter("AppData", WorkingFolder);
			Types.SetModuleParameter("JWT", JwtFactory.CreateHmacSha256("NeuroAli"));

			// Initializing database

			string DataFolder = Path.Combine(WorkingFolder, "Data");
			if (!Directory.Exists(DataFolder))
				Directory.CreateDirectory(DataFolder);

			Types.SetModuleParameter("Data", DataFolder);

			FilesProvider db = await FilesProvider.CreateAsync(DataFolder, "Default",
				8192, 10000, 8192, Encoding.UTF8, 10000, true, true);
			Database.Register(db);

			// Initializing event log

			string EventsFolder = Path.Combine(WorkingFolder, "Events");
			if (!Directory.Exists(EventsFolder))
				Directory.CreateDirectory(EventsFolder);

			Log.Register(new PersistedEventLog(90, new TimeSpan(05, 00, 00)));
			Log.Register(new XmlFileEventSink("Events",
				Path.Combine(EventsFolder, "Event Log %YEAR%-%MONTH%-%DAY%T%HOUR%.xml"),
				Path.Combine(ExecutableFolder, "Transforms", "EventXmlToHtml.xslt"), 7));

			// Initializing local web server

			string HttpLogFolder = Path.Combine(WorkingFolder, "HTTP");
			if (!Directory.Exists(HttpLogFolder))
				Directory.CreateDirectory(HttpLogFolder);

			HttpServer WebServer = new(HttpPort, new XmlFileSniffer(
				Path.Combine(HttpLogFolder, "HTTP %YEAR%-%MONTH%-%DAY%T%HOUR%.xml"),
				Path.Combine(ExecutableFolder, "Transforms", "SnifferXmlToHtml.xslt"),
				7, BinaryPresentationMethod.Hexadecimal));

			// Administration pages (local access only). Markdown and web script must be
			// protected, so their source (including script) is never returned as-is.

			HttpFolderResource.ProtectContentType(Waher.Content.Markdown.MarkdownCodec.ContentType);
			HttpFolderResource.ProtectContentType(Waher.Content.Markdown.Web.WebScript.WsCodec.ContentType);

			AdminPages.Initialize(WorkingFolder, ExecutableFolder);
			LocalOnlyAuthentication LocalOnly = new();

			HttpFolderResource AdminFolder = new("/Admin", Path.Combine(ExecutableFolder, "Root", "Admin"),
				false, false, false, true, LocalOnly);
			AdminFolder.AllowTypeConversion();

			// Remote MCP servers: command-line definitions, followed by those saved on the
			// administration page.

			List<string> RemoteWarnings = [];
			RemoteEndpoints = await RemoteMcpConfiguration.LoadAsync(RemoteEndpoints, RemoteWarnings);

			foreach (string Warning in RemoteWarnings)
			{
				Log.Warning(Warning);
				ConsoleError.WriteLine(Warning);
			}

			// Initializing MCP servers

			string McpLogFolder = Path.Combine(WorkingFolder, "MCP", "Sniffers");
			if (!Directory.Exists(McpLogFolder))
				Directory.CreateDirectory(McpLogFolder);
			
			string McpFilesFolder = Path.Combine(WorkingFolder, "MCP", "Files");
			if (!Directory.Exists(McpFilesFolder))
				Directory.CreateDirectory(McpFilesFolder);

			XmlFileSnifferSet McpSniffers = new(McpLogFolder,
				"MCP %YEAR%-%MONTH%-%DAY%T%HOUR%.xml", TimeSpan.FromHours(8),
				7, BinaryPresentationMethod.Hexadecimal);

			InternetContentMcpServer ContentMcpServer = new("/MCP/Content", McpSniffers);
			EventLogMcpServer EventLogMcpServer = new("/MCP/EventLog", McpSniffers);
			FileStorageMcpServer FileStorageMcpServer = new("/MCP/Files", McpFilesFolder, McpSniffers);
			XmppMcpServer XmppMcpServer = new("/MCP/XMPP", McpSniffers);
			IdentityMcpServer IdentityMcpServer = new CompatibleIdentityMcpServer("/MCP/Identity", XmppMcpServer, McpSniffers);
			PaymentsMcpServer PaymentsMcpServer = new("/MCP/Payments", XmppMcpServer, IdentityMcpServer, McpSniffers);

			WebServer.Register(ContentMcpServer);
			WebServer.Register(EventLogMcpServer);
			WebServer.Register(FileStorageMcpServer);
			WebServer.Register(XmppMcpServer);
			WebServer.Register(IdentityMcpServer);
			WebServer.Register(PaymentsMcpServer);

			HttpMcpServerResource[] McpServers = [ContentMcpServer, EventLogMcpServer, FileStorageMcpServer,
				XmppMcpServer, IdentityMcpServer, PaymentsMcpServer];
			Dictionary<string, HttpMcpServerResource> ToolOwners = new(StringComparer.Ordinal);
			Dictionary<string, HttpMcpServerResource> PromptOwners = new(StringComparer.Ordinal);

			foreach (HttpMcpServerResource Resource in McpServers)
			{
				foreach (var Tool in Resource.GetTools())
					ToolOwners[GetUniqueName(ToolOwners, Tool.Method.Name)] = Resource;

				foreach (var Prompt in Resource.GetPrompts())
					PromptOwners[GetUniqueName(PromptOwners, Prompt.Method.Name)] = Resource;
			}

			StdioMcpServer Mcp = new JoinedStdioMcpServer(
				McpServers,
				"/MCP", "StdioMcpServer", "Joined MCP Server", [], null, McpSniffers);

			WebServer.Register(Mcp);

			// STDIO JSON-RPC environment

			string JsonRpcFolder = Path.Combine(WorkingFolder, "JSON-RPC");
			if (!Directory.Exists(JsonRpcFolder))
				Directory.CreateDirectory(JsonRpcFolder);

			XmlFileSniffer JsonRpcSniffer = new(
				Path.Combine(JsonRpcFolder, "JSON-RPC %YEAR%-%MONTH%-%DAY%T%HOUR%.xml"),
				Path.Combine(ExecutableFolder, "Transforms", "SnifferXmlToHtml.xslt"),
				7, BinaryPresentationMethod.Hexadecimal);

			StdioUser StdioUser = new();
			string BaseUrl = "http://localhost:" + HttpPort.ToString() + "/MCP";
			CommunicationLayer StdioJsonRpcLayer = new(true, JsonRpcSniffer);

			static Task SendEvent(object _, NotificationEventArgs e)
			{
				if (e["data"] is string Data && !string.IsNullOrEmpty(Data))
					ConsoleOut.WriteLine(Data);

				return Task.CompletedTask;
			}

			string? SessionId = null;

			// Remote MCP proxy. Tokens are requested using the same XMPP account as the
			// local XMPP tools (the account linked to the STDIO user).

			async Task<Waher.Networking.XMPP.XmppClient?> GetProxyXmppClient(bool CreateIfNotDefined)
			{
				string? CurrentSessionId = SessionId;
				InternalJsonRpcCall Call = new InternalJsonRpcCall(StdioJsonRpcLayer, StdioUser, BaseUrl, SendEvent, "STDIO");
				if (string.IsNullOrEmpty(CurrentSessionId))
				{
					await XmppMcpServer.ExecuteJsonRpc(Call, """
						{
						  "jsonrpc": "2.0",
						  "id": "discovery-init",
						  "method": "initialize",
						  "params": {
						    "protocolVersion": "2025-11-25",
						    "capabilities": {},
						    "clientInfo": {"name": "Neuro-Ali discovery", "version": "1.0"}
						  }
						}
						""", StdioJsonRpcLayer);
				}
				else
					Call.SetSessionId(CurrentSessionId);

				if (await XmppMcpServer.TryGetMcpSession(Call) is not { } Session)
					return null;

				return await XmppMcpServer.GetClient(XmppMcpServer, Call, StdioUser, Session,
					CreateIfNotDefined);
			}

			XmppAccountSettings.Initialize(XmppMcpServer, StdioUser, BaseUrl, GetProxyXmppClient);

			await using RemoteMcpProxy Proxy = new RemoteMcpProxy(RemoteEndpoints, GetProxyXmppClient, ConsoleOut.WriteLine);

			RemoteMcpConfiguration.SetProxy(Proxy);

			// Starting modules

			await Types.StartAllModules(60000);

			WebServer.Register(AdminFolder);
			WebServer.Register(new SnifferViewerResource("/Admin/Sniffer", LocalOnly));
			WebServer.Register("/Index.md", (Request, Response) =>
				throw new FoundException("/Admin/Remotes.md"),
				Synchronous: true, HandlesSubPaths: false, UserSessions: false, LocalOnly);

			// Main loop

			while (true)
			{
				string? Input = await Console.In.ReadLineAsync();
				if (string.IsNullOrEmpty(Input))
					break;

				try
				{
					JsonObject? Message = null;

					if (Proxy.HasRemotes)
					{
						try
						{
							Message = JsonNode.Parse(Input) as JsonObject;
						}
						catch (JsonException)
						{
							Message = null;
						}

						if (Message is not null)
						{
							if (Proxy.TryHandleClientResponse(Message))
								continue;

							if (Proxy.TryGetTarget(Message, out RemoteMcpServer? Remote, out JsonObject? RemoteParams))
							{
								// Remote calls run in the background, so the STDIO loop keeps
								// reading client input (for instance responses to elicitations).

								string Method = Message["method"]!.ToString();
								JsonNode? ClientId = Message["id"]?.DeepClone();

								_ = Task.Run(async () =>
								{
									string RemoteOutput = await Proxy.ForwardAsync(Remote!, Method,
										RemoteParams!, ClientId);

									if (ClientId is not null)
										ConsoleOut.WriteLine(RemoteOutput);
								});

								continue;
							}
						}
					}

					InternalJsonRpcCall JsonRpcCall = new(StdioJsonRpcLayer,
						StdioUser, BaseUrl, SendEvent, "STDIO");
					if (!string.IsNullOrEmpty(SessionId))
						JsonRpcCall.SetSessionId(SessionId);

					HttpMcpServerResource Target = GetRequestOwner(Input, ToolOwners, PromptOwners) ?? Mcp;
					if (await Target.TryGetMcpSession(JsonRpcCall) is { } ExistingSession)
						Target.RegisterSession(JsonRpcCall, ExistingSession);

					string? CompatibleOutput = Target == IdentityMcpServer ?
						await TryExecuteCompatibleCreateOpenContract(JsonRpcCall, IdentityMcpServer, Input) : null;
					string Output = CompatibleOutput ?? (Target == Mcp ?
						await ExecuteCompleteJoinedToolsList(JsonRpcCall, Mcp, Input, StdioJsonRpcLayer, BaseUrl) :
						await Target.ExecuteJsonRpc(JsonRpcCall, Input, StdioJsonRpcLayer));

					if (JsonRpcCall.TryGetSessionId(out string? NewSessionId))
						SessionId = NewSessionId;

					if (await Target.TryGetMcpSession(JsonRpcCall) is { } CurrentSession)
					{
						Mcp.RegisterSession(JsonRpcCall, CurrentSession);
						foreach (HttpMcpServerResource Owner in McpServers)
							Owner.RegisterSession(JsonRpcCall, CurrentSession);
					}

					if (Message is not null)
					{
						if (!string.IsNullOrEmpty(Output) &&
							RemoteMcpProxy.IsInitialListRequest(Message, out string ListMethod, out string ArrayName))
						{
							Output = await Proxy.AppendRemoteItemsAsync(ListMethod, ArrayName, Output);
						}
						else if (Message["method"]?.ToString() == "tools/call")
							await Proxy.CheckRemotesAvailableAsync();
					}

					if (!string.IsNullOrEmpty(Output))
						ConsoleOut.WriteLine(Output);
				}
				catch (Exception ex)
				{
					ConsoleError.WriteLine(ex.Message);
				}
			}
		}
		catch (Exception ex)
		{
			ConsoleError.WriteLine(ex.Message);
		}
		finally
		{
			await Types.StopAllModules();
		}
	}
}
