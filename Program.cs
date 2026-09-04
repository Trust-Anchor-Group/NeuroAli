using NeuroAli;
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
using Waher.Mcp.Payments;
using Waher.Mcp.Xmpp;
using Waher.Networking;
using Waher.Networking.HTTP;
using Waher.Networking.HTTP.JsonRpc;
using Waher.Networking.HTTP.JsonRpc.Transports;
using Waher.Networking.HTTP.Mcp;
using Waher.Networking.HTTP.Mcp.Model;
using Waher.Networking.Sniffers;
using Waher.Persistence;
using Waher.Persistence.Files;
using Waher.Runtime.Console;
using Waher.Runtime.Inventory;
using Waher.Runtime.Inventory.Loader;
using Waher.Security.JWT;

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

					case "-h":
						Console.Out.WriteLine("Command-line parameters:");
						Console.Out.WriteLine();
						Console.Out.WriteLine("-d FOLDER     Sets the working folder where data is stored to FOLDER.");
						Console.Out.WriteLine("-p PORT       Defines the HTTP port number to use as PORT.");
						Console.Out.WriteLine("-h            Shows command-line parameters available.");
						return;
				}
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
			IdentityMcpServer IdentityMcpServer = new("/MCP/Identity", XmppMcpServer, McpSniffers);
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

			// Starting modules

			await Types.StartAllModules(60000);

			// Main loop

			while (true)
			{
				string? Input = await ConsoleIn.ReadLineAsync();
				if (string.IsNullOrEmpty(Input))
					break;

				try
				{
					InternalJsonRpcCall JsonRpcCall = new(StdioJsonRpcLayer,
						StdioUser, BaseUrl, SendEvent, "STDIO");
					if (!string.IsNullOrEmpty(SessionId))
						JsonRpcCall.SetSessionId(SessionId);

					HttpMcpServerResource Target = GetRequestOwner(Input, ToolOwners, PromptOwners) ?? Mcp;
					if (await Target.TryGetMcpSession(JsonRpcCall) is { } ExistingSession)
						Target.RegisterSession(JsonRpcCall, ExistingSession);

					string Output = Target == Mcp ?
						await ExecuteCompleteJoinedToolsList(JsonRpcCall, Mcp, Input, StdioJsonRpcLayer, BaseUrl) :
						await Target.ExecuteJsonRpc(JsonRpcCall, Input, StdioJsonRpcLayer);

					if (JsonRpcCall.TryGetSessionId(out string? NewSessionId))
						SessionId = NewSessionId;

					if (await Target.TryGetMcpSession(JsonRpcCall) is { } CurrentSession)
						Target.RegisterSession(JsonRpcCall, CurrentSession);

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
