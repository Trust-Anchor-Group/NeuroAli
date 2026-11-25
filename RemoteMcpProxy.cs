using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using Waher.Events;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Events;
using Waher.Security.JWT;

namespace NeuroAli
{
	/// <summary>
	/// Configuration of a remote MCP endpoint that Neuro-Ali proxies.
	/// </summary>
	/// <param name="Alias">Short name, used as prefix for remote tool and prompt names.</param>
	/// <param name="Url">Streamable HTTP MCP endpoint on the remote Neuron.</param>
	/// <param name="Issuer">XMPP address of the JWT issuer, or empty for the connected local broker.</param>
	public sealed partial record RemoteMcpEndpoint(string Alias, Uri Url, string Issuer)
	{
		/// <summary>
		/// Separator between alias and remote name in proxied tool and prompt names.
		/// </summary>
		public const string Separator = "_";

		[GeneratedRegex("^[A-Za-z0-9-]{1,16}$")]
		private static partial Regex AliasRegex();

		/// <summary>
		/// Validates and creates a remote endpoint definition.
		/// </summary>
		public static RemoteMcpEndpoint Create(string Alias, string Url, string? Issuer)
		{
			if (!AliasRegex().IsMatch(Alias))
			{
				throw new Exception("Invalid remote alias: " + Alias +
					" (use 1-16 letters, digits or hyphens; no underscores).");
			}

			if (!Uri.TryCreate(Url, UriKind.Absolute, out Uri? Parsed))
				throw new Exception("Invalid remote MCP URL: " + Url);

			// Bearer tokens must not be sent in clear text, except to the local machine.
			if (Parsed.Scheme != Uri.UriSchemeHttps &&
				!(Parsed.Scheme == Uri.UriSchemeHttp && Parsed.IsLoopback))
			{
				throw new Exception("Remote MCP URL must use HTTPS: " + Url);
			}

			return new RemoteMcpEndpoint(Alias, Parsed, ResolveIssuer(Parsed, Issuer));
		}

		internal static string ResolveIssuer(Uri Url, string? Issuer)
		{
			if (!string.IsNullOrWhiteSpace(Issuer))
				return Issuer.Trim();

			return Url.IsLoopback ? string.Empty : Url.IdnHost;
		}

		internal void ValidateJwtBroker(XmppClient Client)
		{
			if (!this.Url.IsLoopback || this.Issuer.Length != 0)
				return;

			string Host = Client.Host;
			if (!string.Equals(Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
				!(IPAddress.TryParse(Host, out IPAddress? Address) && IPAddress.IsLoopback(Address)))
			{
				throw new RemoteMcpException("Connect an XMPP account on the local Neuron, or specify a JWT issuer for this endpoint.");
			}
		}
	}

	/// <summary>
	/// Provides the XMPP client used to request JWT tokens.
	/// </summary>
	/// <param name="CreateIfNotDefined">If the user may be asked to log in, if no account is connected.</param>
	/// <returns>Connected client, or null if none is available.</returns>
	public delegate Task<XmppClient?> XmppClientProvider(bool CreateIfNotDefined);

	/// <summary>
	/// Error communicating with a remote MCP server.
	/// </summary>
	public class RemoteMcpException(string Message) : Exception(Message)
	{
	}

	/// <summary>
	/// The remote MCP server cannot be used yet, for instance because no XMPP account is connected.
	/// </summary>
	public sealed class RemoteMcpUnavailableException(string Message) : RemoteMcpException(Message)
	{
	}

	/// <summary>
	/// Runtime state of a remote MCP server, as shown on the configuration page.
	/// </summary>
	/// <param name="State">Human-readable state.</param>
	/// <param name="LastError">Last error, with time, if any.</param>
	/// <param name="ToolNames">Tools from the latest successful discovery, if any.</param>
	public sealed record RemoteMcpServerState(string State, string? LastError, string[]? ToolNames);

	/// <summary>
	/// MCP client for one remote Streamable HTTP MCP endpoint, authenticating with JWT
	/// tokens issued over XMPP (urn:xmpp:jwt:0) by the remote Neuron.
	/// </summary>
	internal sealed class RemoteMcpServer : IAsyncDisposable
	{
		private const string ProtocolVersion = "2025-11-25";
		private const string JwtNamespace = "urn:xmpp:jwt:0";
		private const string ProxyIdPrefix = "proxy:";
		private const int TokenSeconds = 300;
		private const int MaxErrorBodyLength = 1024;
		private const int MaxListPages = 50;
		private static readonly TimeSpan TokenMargin = TimeSpan.FromSeconds(30);
		private static readonly TimeSpan TokenRequestTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan IssuerPresenceTimeout = TimeSpan.FromSeconds(2);
		private static readonly TimeSpan MaxSseRetryDelay = TimeSpan.FromSeconds(30);

		private readonly RemoteMcpEndpoint endpoint;
		private readonly XmppClientProvider xmppClients;
		private readonly Action<string> sendToClient;
		private readonly Action<string> listChanged;
		private readonly bool listenForEvents;
		private readonly HttpClient http;
		private readonly SemaphoreSlim sessionLock = new(1, 1);
		private readonly SemaphoreSlim tokenLock = new(1, 1);
		private readonly CancellationTokenSource disposed = new();
		private readonly ConcurrentDictionary<string, PendingServerRequest> pendingServerRequests = new();
		private string? token;
		private DateTime tokenExpires = DateTime.MinValue;
		private string? sessionId;
		private string protocolVersion = ProtocolVersion;
		private JsonObject? serverCapabilities;
		private long nextRequestId;
		private volatile string[]? toolNames;

		private sealed record PendingServerRequest(JsonNode? OriginalId, bool UrlElicitation);

		private sealed record HttpResult(HttpStatusCode Status, JsonObject? Body, string? SessionId);

		/// <summary>
		/// MCP client for one remote Streamable HTTP MCP endpoint.
		/// </summary>
		/// <param name="Endpoint">Endpoint definition.</param>
		/// <param name="XmppClients">Provides the XMPP client used to request tokens.</param>
		/// <param name="SendToClient">Writes a JSON-RPC message to the STDIO client.</param>
		/// <param name="ListChanged">Called with the notification method when a remote list changes.</param>
		/// <param name="ListenForEvents">If the remote event stream should be opened.</param>
		public RemoteMcpServer(RemoteMcpEndpoint Endpoint, XmppClientProvider XmppClients,
			Action<string> SendToClient, Action<string> ListChanged, bool ListenForEvents = true)
		{
			this.endpoint = Endpoint;
			this.xmppClients = XmppClients;
			this.sendToClient = SendToClient;
			this.listChanged = ListChanged;
			this.listenForEvents = ListenForEvents;

			// Redirects are disabled so the bearer token is never sent to another host.
			this.http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = !Endpoint.Url.IsLoopback })
			{
				Timeout = Timeout.InfiniteTimeSpan
			};
		}

		/// <summary>
		/// Alias of the remote endpoint.
		/// </summary>
		public string Alias => this.endpoint.Alias;

		/// <summary>
		/// Prefix used for IDs of requests the remote server sends to the STDIO client.
		/// </summary>
		public string ClientRequestPrefix => ProxyIdPrefix + this.endpoint.Alias + ":";

		private string? lastError;

		/// <summary>
		/// Records an error, to be shown on the configuration page.
		/// </summary>
		public void ReportError(string Message)
		{
			this.lastError = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ": " + Message;
		}

		/// <summary>
		/// Current runtime state.
		/// </summary>
		public RemoteMcpServerState GetState()
		{
			string State;

			if (this.sessionId is not null)
			{
				State = "Connected";
				if (this.token is not null && this.tokenExpires > DateTime.UtcNow)
					State += ", token valid until " + this.tokenExpires.ToLocalTime().ToString("HH:mm:ss");
			}
			else
				State = "Not connected yet";

			return new RemoteMcpServerState(State, this.lastError, this.toolNames);
		}

		/// <summary>
		/// Sends a JSON-RPC request to the remote server and returns its response.
		/// </summary>
		/// <param name="Method">JSON-RPC method.</param>
		/// <param name="Params">Parameters, if any.</param>
		/// <param name="AllowLogin">If the user may be asked to log in to an XMPP account.</param>
		/// <param name="Cancel">Cancellation token.</param>
		/// <returns>JSON-RPC response object.</returns>
		public async Task<JsonObject> RequestAsync(string Method, JsonObject? Params, bool AllowLogin,
			CancellationToken Cancel)
		{
			JsonObject Request = new()
			{
				["jsonrpc"] = "2.0",
				["id"] = "neuroali-" + Interlocked.Increment(ref this.nextRequestId).ToString(),
				["method"] = Method
			};

			if (Params is not null)
				Request["params"] = Params;

			for (int Attempt = 0; ; Attempt++)
			{
				await this.EnsureSessionAsync(AllowLogin, Cancel);

				HttpResult Result = await this.PostAsync(Request, true, AllowLogin, Cancel);

				if (Result.Status == HttpStatusCode.NotFound && Attempt == 0)
				{
					// Session expired on the remote server. Start a new one, and retry once.
					await this.ResetSessionAsync(Result.SessionId);
					continue;
				}

				if (Result.Body is null)
					throw new RemoteMcpException("Remote server returned no response to " + Method + ".");

				return Result.Body;
			}
		}

		/// <summary>
		/// Gets all items of a paginated list (tools/list, prompts/list, resources/list).
		/// </summary>
		public async Task<JsonArray> ListAllAsync(string Method, string ArrayName, bool AllowLogin,
			CancellationToken Cancel)
		{
			JsonArray Items = [];
			HashSet<string> SeenCursors = new(StringComparer.Ordinal);
			string? Cursor = null;

			for (int Page = 0; Page < MaxListPages; Page++)
			{
				JsonObject? Params = Cursor is null ? null : new JsonObject { ["cursor"] = Cursor };
				JsonObject Response = await this.RequestAsync(Method, Params, AllowLogin, Cancel);

				if (Response["error"] is JsonObject Error)
					throw new RemoteMcpException(Method + " failed: " + Error["message"]?.ToString());

				if (Response["result"] is not JsonObject Result || Result[ArrayName] is not JsonArray Page2)
					throw new RemoteMcpException(Method + " returned an invalid result.");

				foreach (JsonNode? Item in Page2)
					Items.Add(Item?.DeepClone());

				Cursor = Result["nextCursor"]?.GetValue<string>();
				if (string.IsNullOrEmpty(Cursor) || !SeenCursors.Add(Cursor))
					break;
			}

			if (Method == "tools/list")
			{
				List<string> ToolNames = new List<string>();
				foreach (JsonObject Item in Items.OfType<JsonObject>())
				{
					if (Item["name"] is JsonValue Name && Name.TryGetValue(out string? ToolName))
						ToolNames.Add(ToolName);
				}

				this.toolNames = ToolNames.ToArray();
			}

			return Items;
		}

		internal async Task<string[]?> DiscoverToolsAsync(CancellationToken Cancel)
		{
			await this.EnsureSessionAsync(false, Cancel);
			if (this.serverCapabilities?["tools"] is JsonObject)
			{
				await this.ListAllAsync("tools/list", "tools", false, Cancel);
				if (this.toolNames is { Length: > 0 } Names)
					return Names;
			}

			if (this.serverCapabilities?["prompts"] is JsonObject &&
				(await this.ListAllAsync("prompts/list", "prompts", false, Cancel)).Count > 0)
			{
				return [];
			}

			return this.serverCapabilities?["resources"] is JsonObject &&
				(await this.ListAllAsync("resources/list", "resources", false, Cancel)).Count > 0 ? [] : null;
		}

		/// <summary>
		/// Handles a response from the STDIO client to a request the remote server sent.
		/// </summary>
		/// <param name="ProxyId">ID used towards the STDIO client.</param>
		/// <param name="Message">Response message.</param>
		public async Task HandleClientResponseAsync(string ProxyId, JsonObject Message)
		{
			if (!this.pendingServerRequests.TryRemove(ProxyId, out PendingServerRequest? Pending))
				return;

			// Waher MCP servers complete URL-mode elicitations through the form at the URL.
			// An "accept" without content would make the remote request fail, so only
			// decline and cancel are forwarded for URL-mode elicitations.
			if (Pending.UrlElicitation &&
				Message["result"] is JsonObject Result &&
				Result["action"]?.ToString() == "accept")
			{
				return;
			}

			JsonObject Forward = (JsonObject)Message.DeepClone();
			Forward["id"] = Pending.OriginalId?.DeepClone();

			await this.PostQuietlyAsync(Forward);
		}

		private async Task EnsureSessionAsync(bool AllowLogin, CancellationToken Cancel)
		{
			if (this.sessionId is not null)
				return;

			await this.sessionLock.WaitAsync(Cancel);
			try
			{
				if (this.sessionId is not null)
					return;

				JsonObject Initialize = new()
				{
					["jsonrpc"] = "2.0",
					["id"] = "neuroali-init-" + Interlocked.Increment(ref this.nextRequestId).ToString(),
					["method"] = "initialize",
					["params"] = new JsonObject
					{
						["protocolVersion"] = ProtocolVersion,
						["capabilities"] = new JsonObject
						{
							// URL mode only: sensitive input, such as XMPP credentials, is then
							// entered in the remote Neuron's own form, never through the agent.
							["elicitation"] = new JsonObject { ["url"] = new JsonObject() }
						},
						["clientInfo"] = new JsonObject
						{
							["name"] = "Neuro-Ali",
							["version"] = typeof(RemoteMcpServer).Assembly.GetName().Version?.ToString() ?? "1.0"
						}
					}
				};

				HttpResult Result = await this.PostAsync(Initialize, true, AllowLogin, Cancel);

				if (Result.Body?["error"] is JsonObject Error)
					throw new RemoteMcpException("initialize failed: " + Error["message"]?.ToString());

				if (Result.Body?["result"] is not JsonObject InitResult)
					throw new RemoteMcpException("initialize returned an invalid result.");

				if (string.IsNullOrEmpty(Result.SessionId))
					throw new RemoteMcpException("Remote server did not return an MCP-Session-Id.");

				this.protocolVersion = InitResult["protocolVersion"]?.ToString() ?? ProtocolVersion;
				this.serverCapabilities = InitResult["capabilities"] as JsonObject;
				this.sessionId = Result.SessionId;

				JsonObject Initialized = new()
				{
					["jsonrpc"] = "2.0",
					["method"] = "notifications/initialized"
				};

				await this.PostAsync(Initialized, false, AllowLogin, Cancel);

				if (this.listenForEvents)
				{
					string SessionId = this.sessionId;
					_ = Task.Run(() => this.ServerEventsLoopAsync(SessionId, this.disposed.Token));
				}
			}
			finally
			{
				this.sessionLock.Release();
			}
		}

		private async Task ResetSessionAsync(string? ExpiredSessionId)
		{
			await this.sessionLock.WaitAsync();
			try
			{
				if (ExpiredSessionId is null || this.sessionId == ExpiredSessionId)
					this.sessionId = null;
			}
			finally
			{
				this.sessionLock.Release();
			}
		}

		private async Task<string> GetTokenAsync(bool AllowLogin, CancellationToken Cancel)
		{
			await this.tokenLock.WaitAsync(Cancel);
			try
			{
				if (this.token is not null && this.tokenExpires - TokenMargin > DateTime.UtcNow)
					return this.token;

				XmppClient? Client = await this.xmppClients(AllowLogin) ??
					throw new RemoteMcpUnavailableException("Neuro-Ali has no connected XMPP " +
						"account. Use one of the XMPP tools to log in first.");

				if (Client.State != XmppState.Connected)
				{
					throw new RemoteMcpUnavailableException("The XMPP account of Neuro-Ali " +
						"is not connected.");
				}

				this.endpoint.ValidateJwtBroker(Client);

				string Recipient = await this.GetJwtRecipientAsync(Client, Cancel);

				// An empty recipient addresses the connected broker without naming a domain.
				XmlElement Response = await Client.IqGetAsync(Recipient,
					"<jwt xmlns='" + JwtNamespace + "' seconds='" + TokenSeconds.ToString() + "'/>")
					.WaitAsync(TokenRequestTimeout, Cancel);

				string? Token = null;

				foreach (XmlNode Node in Response.ChildNodes)
				{
					if (Node is XmlElement E && E.LocalName == "token" && E.NamespaceURI == JwtNamespace)
					{
						Token = E.InnerText.Trim();
						break;
					}
				}

				if (string.IsNullOrEmpty(Token) || Token.Any(char.IsWhiteSpace))
				{
					throw new RemoteMcpException("JWT issuer " + this.endpoint.Issuer +
						" returned no valid token.");
				}

				DateTime Expires = JwtToken.TryParse(Token, out JwtToken Parsed) &&
					Parsed.Expiration is DateTime Expiration ?
					Expiration : DateTime.UtcNow.AddSeconds(TokenSeconds);

				this.token = Token;
				this.tokenExpires = Expires;

				return Token;
			}
			catch (XmppException ex)
			{
				throw new RemoteMcpException("JWT issuer " + this.endpoint.Issuer +
					" refused to issue a token: " + ex.Message);
			}
			catch (TimeoutException)
			{
				throw new RemoteMcpException("JWT issuer " + this.endpoint.Issuer +
					" did not respond in time.");
			}
			finally
			{
				this.tokenLock.Release();
			}
		}

		private async Task<string> GetJwtRecipientAsync(XmppClient Client, CancellationToken Cancel)
		{
			string Issuer = this.endpoint.Issuer;
			if (!XmppClient.BareJidRegEx.IsMatch(Issuer))
				return Issuer;

			string? FindOnlineResource()
			{
				RosterItem? Contact = Client.GetRosterItem(Issuer);
				return Contact?.Resources
					.OrderByDescending(Presence => Presence.Priority)
					.Select(Presence => Presence.From)
					.FirstOrDefault(Jid => Jid.Length > Issuer.Length + 1 &&
						Jid.StartsWith(Issuer + "/", StringComparison.OrdinalIgnoreCase));
			}

			string? Recipient = FindOnlineResource();
			if (Recipient is not null)
				return Recipient;

			TaskCompletionSource<string> Available = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			Task OnPresence(object Sender, PresenceEventArgs Presence)
			{
				if (string.Equals(Presence.FromBareJID, Issuer, StringComparison.OrdinalIgnoreCase) &&
					FindOnlineResource() is string Online)
				{
					Available.TrySetResult(Online);
				}

				return Task.CompletedTask;
			}

			// The roster's initial presence can arrive just after the client connects.
			Client.OnPresence += OnPresence;
			try
			{
				return FindOnlineResource() ?? await Available.Task.WaitAsync(IssuerPresenceTimeout, Cancel);
			}
			catch (TimeoutException)
			{
				throw new RemoteMcpException("No online XMPP resource was found for JWT issuer " + Issuer +
					". Check that the Neuron is online and has approved this account's presence subscription, or enter its full JID.");
			}
			finally
			{
				Client.OnPresence -= OnPresence;
			}
		}

		private void InvalidateToken(string Token)
		{
			if (this.token == Token)
				this.token = null;
		}

		private HttpRequestMessage CreateHttpRequest(HttpMethod Method, string Token, string Accept)
		{
			HttpRequestMessage Request = new(Method, this.endpoint.Url);

			Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
			Request.Headers.Accept.ParseAdd(Accept);
			Request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", this.protocolVersion);

			if (this.sessionId is not null)
				Request.Headers.TryAddWithoutValidation("MCP-Session-Id", this.sessionId);

			return Request;
		}

		private async Task<HttpResult> PostAsync(JsonObject Message, bool ExpectResponse,
			bool AllowLogin, CancellationToken Cancel)
		{
			string Body = Message.ToJsonString();

			for (int Attempt = 0; ; Attempt++)
			{
				string Token = await this.GetTokenAsync(AllowLogin, Cancel);
				string? SentSessionId = this.sessionId;
				using HttpRequestMessage Request = this.CreateHttpRequest(HttpMethod.Post, Token,
					"application/json, text/event-stream");

				Request.Content = new StringContent(Body, Encoding.UTF8, "application/json");

				using HttpResponseMessage Response = await this.http.SendAsync(Request,
					HttpCompletionOption.ResponseHeadersRead, Cancel);

				if (Response.StatusCode == HttpStatusCode.Unauthorized)
				{
					this.InvalidateToken(Token);

					if (Attempt == 0)
						continue;

					throw new RemoteMcpException("Remote server rejected the token: " +
						GetBearerError(Response));
				}

				if (Response.StatusCode == HttpStatusCode.Forbidden)
				{
					throw new RemoteMcpException("Access denied by remote server: " +
						await ReadErrorBody(Response, Cancel));
				}

				string? ResponseSessionId = Response.Headers.TryGetValues("MCP-Session-Id",
					out IEnumerable<string>? Values) ? Values.FirstOrDefault() : null;

				if (Response.StatusCode == HttpStatusCode.NotFound && SentSessionId is not null)
					return new HttpResult(Response.StatusCode, null, SentSessionId);

				if (!Response.IsSuccessStatusCode)
				{
					throw new RemoteMcpException("Remote server returned " + ((int)Response.StatusCode).ToString() +
						": " + await ReadErrorBody(Response, Cancel));
				}

				if (!ExpectResponse || Response.StatusCode == HttpStatusCode.Accepted)
					return new HttpResult(Response.StatusCode, null, ResponseSessionId);

				string? MediaType = Response.Content.Headers.ContentType?.MediaType;
				JsonObject? Result;

				if (MediaType == "text/event-stream")
				{
					string Id = Message["id"]?.ToString() ?? string.Empty;
					Result = null;

					await using Stream Stream = await Response.Content.ReadAsStreamAsync(Cancel);
					await ReadServerSentEvents(Stream, Data =>
					{
						if (Result is null && TryParseObject(Data) is JsonObject Obj &&
							Obj["method"] is null && Obj["id"]?.ToString() == Id)
						{
							Result = Obj;
							return false;
						}

						this.HandleServerMessage(Data);
						return true;
					}, Cancel);
				}
				else
					Result = JsonNode.Parse(await Response.Content.ReadAsStringAsync(Cancel)) as JsonObject;

				return new HttpResult(Response.StatusCode, Result, ResponseSessionId);
			}
		}

		/// <summary>
		/// Keeps a GET SSE stream open, so the remote server can send requests (such as
		/// elicitations) and notifications. Waher MCP servers only push such messages to
		/// sessions with an open stream.
		/// </summary>
		private async Task ServerEventsLoopAsync(string SessionId, CancellationToken Cancel)
		{
			TimeSpan Delay = TimeSpan.FromSeconds(1);

			while (!Cancel.IsCancellationRequested && this.sessionId == SessionId)
			{
				try
				{
					string Token = await this.GetTokenAsync(false, Cancel);
					using HttpRequestMessage Request = this.CreateHttpRequest(HttpMethod.Get, Token,
						"text/event-stream");
					using HttpResponseMessage Response = await this.http.SendAsync(Request,
						HttpCompletionOption.ResponseHeadersRead, Cancel);

					if (Response.StatusCode == HttpStatusCode.NotFound)
					{
						await this.ResetSessionAsync(SessionId);
						return;
					}

					if (Response.StatusCode == HttpStatusCode.Unauthorized)
						this.InvalidateToken(Token);
					else if (Response.IsSuccessStatusCode &&
						Response.Content.Headers.ContentType?.MediaType == "text/event-stream")
					{
						Delay = TimeSpan.FromSeconds(1);

						await using Stream Stream = await Response.Content.ReadAsStreamAsync(Cancel);
						await ReadServerSentEvents(Stream, Data =>
						{
							this.HandleServerMessage(Data);
							return true;
						}, Cancel);
					}
					else if (Response.StatusCode == HttpStatusCode.MethodNotAllowed)
						return;     // Server does not offer a server-to-client stream.
				}
				catch (Exception) when (Cancel.IsCancellationRequested)
				{
					return;
				}
				catch (Exception ex)
				{
					this.ReportError("Event stream interrupted: " + ex.Message);
					Log.Warning("Event stream from remote MCP server interrupted: " + ex.Message,
						this.endpoint.Alias);
				}

				try
				{
					await Task.Delay(Delay, Cancel);
				}
				catch (OperationCanceledException)
				{
					return;
				}

				Delay = TimeSpan.FromTicks(Math.Min(Delay.Ticks * 2, MaxSseRetryDelay.Ticks));
			}
		}

		private static JsonObject? TryParseObject(string Data)
		{
			try
			{
				return JsonNode.Parse(Data) as JsonObject;
			}
			catch (Exception)
			{
				return null;
			}
		}

		private async Task PostQuietlyAsync(JsonObject Message)
		{
			try
			{
				await this.PostAsync(Message, false, false, this.disposed.Token);
			}
			catch (Exception)
			{
				// Best effort.
			}
		}

		private void HandleServerMessage(string Data)
		{
			JsonObject? Message = TryParseObject(Data);

			if (Message?["method"]?.ToString() is not string Method)
				return;     // Responses on the event stream are not expected.

			if (Message["id"] is JsonNode Id)
			{
				// Request from remote server to the client.

				if (Method == "ping")
				{
					JsonObject Pong = new()
					{
						["jsonrpc"] = "2.0",
						["id"] = Id.DeepClone(),
						["result"] = new JsonObject()
					};

					_ = this.PostQuietlyAsync(Pong);
					return;
				}

				bool UrlElicitation = Method == "elicitation/create" &&
					(Message["params"] as JsonObject)?["mode"]?.ToString() == "url";
				string ProxyId = this.ClientRequestPrefix + Guid.NewGuid().ToString("N");

				this.pendingServerRequests[ProxyId] = new PendingServerRequest(Id.DeepClone(), UrlElicitation);
				Message["id"] = ProxyId;

				if (Method == "elicitation/create" && Message["params"] is JsonObject Params &&
					Params["message"]?.ToString() is string Text)
				{
					Params["message"] = "[" + this.endpoint.Alias + " @ " + this.endpoint.Url.Host + "] " + Text;
				}

				this.sendToClient(Message.ToJsonString());
				return;
			}

			// Notification from remote server.

			switch (Method)
			{
				case "notifications/tools/list_changed":
				case "notifications/prompts/list_changed":
				case "notifications/resources/list_changed":
					this.listChanged(Method);
					break;

				default:
					this.sendToClient(Message.ToJsonString());
					break;
			}
		}

		private static async Task ReadServerSentEvents(Stream Stream, Func<string, bool> OnData,
			CancellationToken Cancel)
		{
			using StreamReader Reader = new(Stream, Encoding.UTF8);
			StringBuilder Data = new();

			while (await Reader.ReadLineAsync(Cancel) is string Line)
			{
				if (Line.Length == 0)
				{
					if (Data.Length > 0)
					{
						string s = Data.ToString();
						Data.Clear();

						if (!OnData(s))
							return;
					}
				}
				else if (Line.StartsWith("data:", StringComparison.Ordinal))
				{
					if (Data.Length > 0)
						Data.Append('\n');

					Data.Append(Line.AsSpan(Line.Length > 5 && Line[5] == ' ' ? 6 : 5));
				}
				// Comments (":") and other fields (event, id, retry) are ignored.
			}
		}

		private static string GetBearerError(HttpResponseMessage Response)
		{
			foreach (AuthenticationHeaderValue Challenge in Response.Headers.WwwAuthenticate)
			{
				if (string.Equals(Challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
					!string.IsNullOrEmpty(Challenge.Parameter))
				{
					return Challenge.Parameter;
				}
			}

			return "no reason given";
		}

		private static async Task<string> ReadErrorBody(HttpResponseMessage Response, CancellationToken Cancel)
		{
			try
			{
				string s = await Response.Content.ReadAsStringAsync(Cancel);
				if (s.Length > MaxErrorBodyLength)
					s = s[..MaxErrorBodyLength] + "...";

				return string.IsNullOrWhiteSpace(s) ? Response.ReasonPhrase ?? string.Empty : s;
			}
			catch (Exception)
			{
				return Response.ReasonPhrase ?? string.Empty;
			}
		}

		/// <summary>
		/// Closes the remote session and stops the event stream.
		/// </summary>
		public async ValueTask DisposeAsync()
		{
			this.disposed.Cancel();

			if (this.sessionId is not null && this.token is not null)
			{
				try
				{
					using CancellationTokenSource Timeout = new(TimeSpan.FromSeconds(2));
					using HttpRequestMessage Request = this.CreateHttpRequest(HttpMethod.Delete,
						this.token, "application/json");
					using HttpResponseMessage _ = await this.http.SendAsync(Request, Timeout.Token);
				}
				catch (Exception)
				{
					// Best effort. The remote session expires by itself.
				}
			}

			this.http.Dispose();
			this.disposed.Dispose();
		}
	}

	/// <summary>
	/// Proxies remote MCP endpoints through Neuro-Ali's STDIO interface. Remote tools and
	/// prompts are exposed as ALIAS_NAME, remote resources keep their URIs.
	/// </summary>
	internal sealed class RemoteMcpProxy : IAsyncDisposable
	{
		private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(20);
		private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(10);

		private readonly ConcurrentDictionary<string, RemoteMcpServer> remotes = new(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, RemoteMcpServer> resourceOwners = new(StringComparer.Ordinal);
		private readonly ConcurrentDictionary<string, bool> unavailable = new(StringComparer.OrdinalIgnoreCase);
		private readonly XmppClientProvider xmppClients;
		private readonly Action<string> sendToClient;

		/// <summary>
		/// Proxies remote MCP endpoints through Neuro-Ali's STDIO interface.
		/// </summary>
		/// <param name="Endpoints">Remote endpoints.</param>
		/// <param name="XmppClients">Provides the XMPP client used to request tokens.</param>
		/// <param name="SendToClient">Writes a JSON-RPC message to the STDIO client.</param>
		public RemoteMcpProxy(IEnumerable<RemoteMcpEndpoint> Endpoints, XmppClientProvider XmppClients,
			Action<string> SendToClient)
		{
			this.xmppClients = XmppClients;
			this.sendToClient = SendToClient;

			foreach (RemoteMcpEndpoint Endpoint in Endpoints)
			{
				if (this.remotes.ContainsKey(Endpoint.Alias))
					throw new Exception("Duplicate remote alias: " + Endpoint.Alias);

				this.remotes[Endpoint.Alias] = new RemoteMcpServer(Endpoint, XmppClients,
					SendToClient, this.ForwardListChanged);
			}
		}

		private void ForwardListChanged(string Method)
		{
			JsonObject Notification = new()
			{
				["jsonrpc"] = "2.0",
				["method"] = Method
			};

			this.sendToClient(Notification.ToJsonString());
		}

		internal bool HasRemotes => !this.remotes.IsEmpty;

		internal async Task<(Uri Url, string[] ToolNames)[]> DiscoverAsync(RemoteMcpEndpoint Host)
		{
			XmppClient? Client = await this.xmppClients(false);
			if (Client is null || Client.State != XmppState.Connected)
				throw new RemoteMcpUnavailableException("Connect a saved XMPP account before discovering Neuron servers.");

			Host.ValidateJwtBroker(Client);
			return await NeuronMcpDiscovery.FindAsync(Host.Url, Host.Issuer, Client);
		}

		internal void Enable(RemoteMcpEndpoint Endpoint)
		{
			if (this.remotes.ContainsKey(Endpoint.Alias))
				return;

			RemoteMcpServer Server = new RemoteMcpServer(Endpoint, this.xmppClients,
				this.sendToClient, this.ForwardListChanged);
			this.remotes[Endpoint.Alias] = Server;
			this.NotifyListsChanged();
		}

		internal async Task DisableAsync(string Alias)
		{
			if (!this.remotes.TryRemove(Alias, out RemoteMcpServer? Server))
				return;

			this.unavailable.TryRemove(Alias, out _);
			foreach (KeyValuePair<string, RemoteMcpServer> Owner in this.resourceOwners)
			{
				if (ReferenceEquals(Owner.Value, Server))
					this.resourceOwners.TryRemove(Owner);
			}

			this.NotifyListsChanged();
			await Server.DisposeAsync();
		}

		private bool IsActive(RemoteMcpServer Server)
		{
			return this.remotes.TryGetValue(Server.Alias, out RemoteMcpServer? Current) &&
				ReferenceEquals(Server, Current);
		}

		private void NotifyListsChanged()
		{
			this.ForwardListChanged("notifications/tools/list_changed");
			this.ForwardListChanged("notifications/prompts/list_changed");
			this.ForwardListChanged("notifications/resources/list_changed");
		}

		/// <summary>
		/// Handles a client response to a request a remote server sent through the proxy.
		/// </summary>
		/// <returns>If the message was a response to a proxied request.</returns>
		public bool TryHandleClientResponse(JsonObject Message)
		{
			if (Message["method"] is not null ||
				Message["id"] is not JsonValue IdValue ||
				!IdValue.TryGetValue(out string? Id) ||
				!Id.StartsWith("proxy:", StringComparison.Ordinal))
			{
				return false;
			}

			foreach (RemoteMcpServer Remote in this.remotes.Values)
			{
				if (Id.StartsWith(Remote.ClientRequestPrefix, StringComparison.Ordinal))
				{
					_ = Remote.HandleClientResponseAsync(Id, Message);
					break;
				}
			}

			return true;
		}

		/// <summary>
		/// Checks if a client request should be forwarded to a remote server.
		/// </summary>
		/// <param name="Message">Client request.</param>
		/// <param name="Remote">Remote server to forward the request to.</param>
		/// <param name="Params">Parameters to send to the remote server.</param>
		/// <returns>If the request targets a remote server.</returns>
		public bool TryGetTarget(JsonObject Message, out RemoteMcpServer? Remote, out JsonObject? Params)
		{
			Remote = null;
			Params = null;

			string? Method = Message["method"]?.ToString();
			if (Message["params"] is not JsonObject Original)
				return false;

			switch (Method)
			{
				case "tools/call":
				case "prompts/get":
					if (Original["name"]?.ToString() is not string Name)
						return false;

					int i = Name.IndexOf(RemoteMcpEndpoint.Separator, StringComparison.Ordinal);
					if (i <= 0 || !this.remotes.TryGetValue(Name[..i], out Remote))
						return false;

					Params = (JsonObject)Original.DeepClone();
					Params["name"] = Name[(i + RemoteMcpEndpoint.Separator.Length)..];
					return true;

				case "resources/read":
					if (Original["uri"]?.ToString() is not string Uri ||
						!this.resourceOwners.TryGetValue(Uri, out Remote) || !this.IsActive(Remote))
					{
						return false;
					}

					Params = (JsonObject)Original.DeepClone();
					return true;

				default:
					return false;
			}
		}

		/// <summary>
		/// Forwards a client request to a remote server.
		/// </summary>
		/// <returns>JSON-RPC response to return to the client, using the client's request ID.</returns>
		public async Task<string> ForwardAsync(RemoteMcpServer Remote, string Method, JsonObject Params,
			JsonNode? ClientId)
		{
			JsonObject Response;

			try
			{
				using CancellationTokenSource Timeout = new(CallTimeout);
				JsonObject Remote2 = await Remote.RequestAsync(Method, Params, true, Timeout.Token);

				Response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ClientId?.DeepClone() };

				if (Remote2["error"] is JsonNode Error)
					Response["error"] = Error.DeepClone();
				else
					Response["result"] = Remote2["result"]?.DeepClone() ?? new JsonObject();
			}
			catch (Exception ex)
			{
				string Message = "Remote MCP server '" + Remote.Alias + "': " +
					(ex is OperationCanceledException ? "request timed out." : ex.Message);

				Remote.ReportError(Method + ": " + (ex is OperationCanceledException ? "request timed out." : ex.Message));

				Response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ClientId?.DeepClone() };

				if (Method == "tools/call")
				{
					// Tool errors are reported as results, so the agent can act on them.
					Response["result"] = new JsonObject
					{
						["content"] = new JsonArray(new JsonObject
						{
							["type"] = "text",
							["text"] = Message
						}),
						["isError"] = true
					};
				}
				else
				{
					Response["error"] = new JsonObject
					{
						["code"] = -32603,
						["message"] = Message
					};
				}
			}

			return Response.ToJsonString();
		}

		/// <summary>
		/// Checks if a request is the first page of a list request whose result should
		/// include remote items.
		/// </summary>
		public static bool IsInitialListRequest(JsonObject Message, out string Method, out string ArrayName)
		{
			Method = Message["method"]?.ToString() ?? string.Empty;
			ArrayName = Method switch
			{
				"tools/list" => "tools",
				"prompts/list" => "prompts",
				"resources/list" => "resources",
				_ => string.Empty
			};

			if (ArrayName.Length == 0 || Message["id"] is null)
				return false;

			return (Message["params"] as JsonObject)?["cursor"] is null;
		}

		/// <summary>
		/// Adds items from all remote servers to a local list response.
		/// </summary>
		public async Task<string> AppendRemoteItemsAsync(string Method, string ArrayName, string LocalOutput)
		{
			JsonObject? Response;

			try
			{
				Response = JsonNode.Parse(LocalOutput) as JsonObject;
			}
			catch (Exception)
			{
				return LocalOutput;
			}

			if (Response?["result"]?[ArrayName] is not JsonArray Items)
				return LocalOutput;

			using CancellationTokenSource Timeout = new(ListTimeout);

			(RemoteMcpServer Remote, Task<JsonArray> Task)[] Requests = [.. this.remotes.Values.Select(Remote =>
				(Remote, Remote.ListAllAsync(Method, ArrayName, false, Timeout.Token)))];

			foreach ((RemoteMcpServer Remote, Task<JsonArray> Task) in Requests)
			{
				JsonArray RemoteItems;

				try
				{
					RemoteItems = await Task;
					if (!this.IsActive(Remote))
						continue;

					this.unavailable.TryRemove(Remote.Alias, out _);
				}
				catch (Exception ex)
				{
					if (ex is RemoteMcpUnavailableException)
						this.unavailable[Remote.Alias] = true;

					Remote.ReportError(Method + ": " + (ex is OperationCanceledException ? "timeout." : ex.Message));

					Log.Warning("Unable to list " + ArrayName + " of remote MCP server: " +
						(ex is OperationCanceledException ? "timeout." : ex.Message), Remote.Alias);
					continue;
				}

				foreach (JsonNode? Item in RemoteItems)
				{
					if (Item?.DeepClone() is not JsonObject Obj)
						continue;

					if (ArrayName == "resources")
					{
						if (Obj["uri"]?.ToString() is string Uri)
							this.resourceOwners[Uri] = Remote;
					}
					else if (Obj["name"]?.ToString() is string Name)
						Obj["name"] = Remote.Alias + RemoteMcpEndpoint.Separator + Name;

					string Label = "[" + Remote.Alias + "] ";

					if (Obj["description"]?.ToString() is string Description)
						Obj["description"] = Label + Description;
					else
						Obj["description"] = Label.TrimEnd();

					if (Obj["title"]?.ToString() is string Title)
						Obj["title"] = Label + Title;

					Items.Add(Obj);
				}
			}

			return Response.ToJsonString();
		}

		/// <summary>
		/// If some remote server could not be listed because no XMPP account was connected,
		/// checks if one is connected now, and tells the client to list tools again.
		/// </summary>
		public async Task CheckRemotesAvailableAsync()
		{
			if (this.unavailable.IsEmpty)
				return;

			try
			{
				if (await this.xmppClients(false) is XmppClient Client && Client.State == XmppState.Connected)
				{
					this.unavailable.Clear();
					this.NotifyListsChanged();
				}
			}
			catch (Exception)
			{
				// Checked again after the next tool call.
			}
		}

		/// <summary>
		/// Gets the runtime state of a remote server.
		/// </summary>
		/// <param name="Alias">Alias of remote server.</param>
		/// <returns>State, or null if no remote server has the alias.</returns>
		public RemoteMcpServerState? GetState(string Alias)
		{
			return this.remotes.TryGetValue(Alias, out RemoteMcpServer? Remote) ? Remote.GetState() : null;
		}

		/// <summary>
		/// Closes all remote sessions.
		/// </summary>
		public async ValueTask DisposeAsync()
		{
			foreach (RemoteMcpServer Remote in this.remotes.Values)
				await Remote.DisposeAsync();

			this.remotes.Clear();
		}
	}
}
