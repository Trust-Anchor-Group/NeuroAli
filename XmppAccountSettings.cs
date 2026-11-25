using System.Text.Json.Nodes;
using Waher.Mcp.Xmpp;
using Waher.Networking;
using Waher.Networking.HTTP.JsonRpc;
using Waher.Networking.HTTP.JsonRpc.Transports;
using Waher.Networking.HTTP.Mcp.Model.Server;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.Events;
using Waher.Persistence;
using Waher.Persistence.Filters;
using Waher.Security;

namespace NeuroAli
{
	/// <summary>
	/// The XMPP account Neuro-Ali uses, as shown on the administration page.
	/// </summary>
	/// <param name="Account">Account name.</param>
	/// <param name="Domain">Domain of the XMPP server.</param>
	/// <param name="TrustServer">If the server certificate is trusted even if it is invalid.</param>
	/// <param name="AllowInsecureMechanisms">If insecure authentication mechanisms are allowed.</param>
	public sealed record XmppAccountInfo(string Account, string Domain, bool TrustServer,
		bool AllowInsecureMechanisms)
	{
		/// <summary>
		/// Bare JID of the account.
		/// </summary>
		public string BareJid => this.Account + "@" + this.Domain;
	}

	/// <summary>
	/// Views and changes the XMPP account used by Neuro-Ali's STDIO interface.
	///
	/// The account is stored by the XMPP MCP server in <see cref="ClientCredentials"/>, which
	/// only lets the XMPP MCP server itself handle the password hash. To set an account, the
	/// administration page therefore uses the XMPP MCP server's own login flow: it opens a
	/// private MCP session, asks the server for a client, and answers the server's request for
	/// credentials with the values entered on the page. The server logs in, which verifies the
	/// password, and saves the account itself.
	/// </summary>
	public static class XmppAccountSettings
	{
		private const string ProtocolVersion = "2025-11-25";

		private static readonly SemaphoreSlim saveLock = new(1, 1);
		private static XmppMcpServer? server = null;
		private static IUser? user = null;
		private static string baseUrl = string.Empty;
		private static XmppClientProvider? xmppClients;

		/// <summary>
		/// Sets the XMPP MCP server and the user whose account is managed.
		/// </summary>
		internal static void Initialize(XmppMcpServer Server, IUser User, string BaseUrl, XmppClientProvider XmppClients)
		{
			server = Server;
			user = User;
			baseUrl = BaseUrl;
			xmppClients = XmppClients;
		}

		private static string McpUserName => user?.UserName ?? "STDIO";

		/// <summary>
		/// Gets the saved account, or null if none is saved.
		/// </summary>
		public static async Task<XmppAccountInfo?> GetAsync()
		{
			foreach (ClientCredentials Credentials in await Database.Find<ClientCredentials>(
				new FilterFieldEqualTo("McpUserName", McpUserName)))
			{
				return new XmppAccountInfo(Credentials.XmppAccountName ?? string.Empty,
					Credentials.Domain ?? string.Empty, Credentials.TrustServer,
					Credentials.AllowInsecureMechanisms);
			}

			return null;
		}

		public static async Task<string[]> GetPresenceRequestsAsync()
		{
			XmppClient Client = await GetConnectedClientAsync();

			return Client.SubscriptionRequests.Select(Request => Request.FromBareJID)
				.OrderBy(Jid => Jid, StringComparer.OrdinalIgnoreCase).ToArray();
		}

		public static async Task SendPresenceRequestAsync(string ContactJid)
		{
			ContactJid = ContactJid.Trim();
			if (!XmppClient.BareJidRegEx.IsMatch(ContactJid))
				throw new ArgumentException("Enter a bare XMPP address such as name@example.com.");

			XmppClient Client = await GetConnectedClientAsync();
			await Client.RequestPresenceSubscription(ContactJid);
		}

		public static async Task AcceptPresenceRequestAsync(string ContactJid, bool RequestBack)
		{
			XmppClient Client = await GetConnectedClientAsync();
			PresenceEventArgs? Request = Client.GetSubscriptionRequest(ContactJid);
			if (Request is null)
				throw new InvalidOperationException("That presence request is no longer pending. Refresh and try again.");

			await Request.Accept();
			if (Client.TryGetExtension(out McpXmppExtension? Extension) && Extension is not null)
				Extension.TryGetPresenceSubscriptionRequest(Request.FromBareJID, out _);

			if (!RequestBack)
				return;

			try
			{
				await Client.RequestPresenceSubscription(Request.FromBareJID);
			}
			catch (Exception Error)
			{
				throw new InvalidOperationException("The incoming request was accepted, but sending a request back failed. " +
					Error.Message, Error);
			}
		}

		private static async Task<XmppClient> GetConnectedClientAsync()
		{
			XmppClientProvider GetClient = xmppClients ?? throw new InvalidOperationException("The XMPP client is unavailable.");
			if (await GetClient(false) is XmppClient Client && Client.State == XmppState.Connected)
				return Client;

			throw new InvalidOperationException("Connect an XMPP account before managing presence subscriptions.");
		}

		/// <summary>
		/// Removes the saved account. If Neuro-Ali is connected, the connection is kept until
		/// Neuro-Ali restarts.
		/// </summary>
		/// <returns>If an account was removed.</returns>
		public static async Task<bool> RemoveAsync()
		{
			await saveLock.WaitAsync();
			try
			{
				IEnumerable<ClientCredentials> Deleted = await Database.FindDelete<ClientCredentials>(
					new FilterFieldEqualTo("McpUserName", McpUserName));

				return Deleted.Any();
			}
			finally
			{
				saveLock.Release();
			}
		}

		/// <summary>
		/// Logs in to an XMPP account and saves it, using the XMPP MCP server's own login flow.
		/// If Neuro-Ali is not connected yet, the account is used right away.
		/// </summary>
		/// <param name="Account">Account name, or bare JID (account@domain).</param>
		/// <param name="Domain">Domain. Can be empty if Account is a bare JID.</param>
		/// <param name="Password">Password.</param>
		/// <param name="TrustServer">If the server certificate is trusted even if it is invalid.</param>
		/// <param name="AllowInsecureMechanisms">If insecure authentication mechanisms are allowed.</param>
		/// <returns>Saved account.</returns>
		/// <exception cref="Exception">If the input is invalid, login fails, or Neuro-Ali is
		/// already connected with another account.</exception>
		public static async Task<XmppAccountInfo> SaveAsync(string Account, string Domain, string Password,
			object? TrustServer, object? AllowInsecureMechanisms)
		{
			XmppMcpServer Server = server ?? throw new Exception("XMPP MCP server not available.");
			IUser User = user ?? throw new Exception("XMPP MCP server not available.");

			Account = Account?.Trim() ?? string.Empty;
			Domain = Domain?.Trim() ?? string.Empty;
			Password ??= string.Empty;

			bool Trust = IsChecked(TrustServer);
			bool Insecure = IsChecked(AllowInsecureMechanisms);

			int i = Account.IndexOf('@');
			if (i > 0)
			{
				if (string.IsNullOrEmpty(Domain))
					Domain = Account[(i + 1)..];
				else if (!string.Equals(Domain, Account[(i + 1)..], StringComparison.OrdinalIgnoreCase))
					throw new Exception("The domain of the account does not match the domain field.");

				Account = Account[..i];
			}

			if (string.IsNullOrEmpty(Account) || Account.Any(char.IsWhiteSpace))
				throw new Exception("Enter a valid account name.");

			if (string.IsNullOrEmpty(Domain) || Uri.CheckHostName(Domain) == UriHostNameType.Unknown)
				throw new Exception("Enter a valid domain.");

			if (string.IsNullOrEmpty(Password))
				throw new Exception("Enter the password.");

			await saveLock.WaitAsync();
			try
			{
				// No sniffer: the credentials pass through this layer.
				CommunicationLayer Layer = new(true);
				string? SessionId = null;
				string? LoginError = null;
				int NrRequests = 0;

				Task OnEvent(object _, NotificationEventArgs e)
				{
					if (e["data"] is not string Data)
						return Task.CompletedTask;

					JsonObject? Request;

					try
					{
						Request = JsonNode.Parse(Data) as JsonObject;
					}
					catch (Exception)
					{
						return Task.CompletedTask;
					}

					if (Request?["method"]?.ToString() != "elicitation/create" || Request["id"] is not JsonNode Id)
						return Task.CompletedTask;

					JsonObject Result;

					if (Interlocked.Increment(ref NrRequests) == 1)
					{
						Result = new JsonObject
						{
							["action"] = "accept",
							["content"] = new JsonObject
							{
								["Domain"] = Domain,
								["UserName"] = Account,
								["Password"] = Password,
								["TrustServer"] = Trust,
								["AllowInsecureMechanisms"] = Insecure,
								["CreateAccount"] = false
							}
						};
					}
					else
					{
						// The server asks again when login fails, with the error first in the message.
						LoginError = GetLoginError((Request["params"] as JsonObject)?["message"]?.ToString());
						Result = new JsonObject { ["action"] = "cancel" };
					}

					string Response = new JsonObject
					{
						["jsonrpc"] = "2.0",
						["id"] = Id.DeepClone(),
						["result"] = Result
					}.ToJsonString();

					// Responses are processed separately, since the server is waiting for them.
					_ = Task.Run(async () =>
					{
						InternalJsonRpcCall ResponseCall = new(Layer, User, baseUrl, OnEvent, "Admin");
						if (SessionId is not null)
							ResponseCall.SetSessionId(SessionId);

						await Server.ExecuteJsonRpc(ResponseCall, Response, Layer);
					});

					return Task.CompletedTask;
				}

				// Private MCP session, declaring support for input forms.

				InternalJsonRpcCall Call = new(Layer, User, baseUrl, OnEvent, "Admin");
				string Initialize = new JsonObject
				{
					["jsonrpc"] = "2.0",
					["id"] = "admin-init",
					["method"] = "initialize",
					["params"] = new JsonObject
					{
						["protocolVersion"] = ProtocolVersion,
						["capabilities"] = new JsonObject
						{
							["elicitation"] = new JsonObject { ["form"] = new JsonObject() }
						},
						["clientInfo"] = new JsonObject
						{
							["name"] = "Neuro-Ali administration page",
							["version"] = "1.0"
						}
					}
				}.ToJsonString();

				await Server.ExecuteJsonRpc(Call, Initialize, Layer);

				if (!Call.TryGetSessionId(out SessionId) || string.IsNullOrEmpty(SessionId))
					throw new Exception("Unable to open a session with the XMPP MCP server.");

				InternalJsonRpcCall InitializedCall = new(Layer, User, baseUrl, OnEvent, "Admin");
				InitializedCall.SetSessionId(SessionId);
				await Server.ExecuteJsonRpc(InitializedCall,
					"{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", Layer);

				Session Session = await Server.TryGetMcpSession(Call) ??
					throw new Exception("Unable to open a session with the XMPP MCP server.");

				Server.RegisterSession(Call, Session);

				// Remove the saved account, so the server asks for credentials.

				await Database.FindDelete<ClientCredentials>(new FilterFieldEqualTo("McpUserName", User.UserName));

				// Without saved credentials, a client is only returned if one is already connected.

				if (await Server.GetClient(Server, Call, User, Session, false) is XmppClient Connected)
				{
					throw new Exception("Neuro-Ali is already connected as " + Connected.BareJID +
						". The saved account was removed, but the connection stays until Neuro-Ali " +
						"restarts. Restart Neuro-Ali, and set the account here before any XMPP tool is used.");
				}

				XmppClient? Client;

				try
				{
					Client = await Server.GetClient(Server, Call, User, Session, true);
				}
				catch (Exception ex)
				{
					throw new Exception(LoginError ?? ex.Message);
				}

				if (Client is null)
					throw new Exception(LoginError ?? "Unable to log in.");

				return new XmppAccountInfo(Account, Domain, Trust, Insecure);
			}
			finally
			{
				saveLock.Release();
			}
		}

		private static string? GetLoginError(string? Message)
		{
			if (string.IsNullOrEmpty(Message) || !Message.StartsWith("Error: ", StringComparison.Ordinal))
				return null;

			int i = Message.IndexOf("\r\n\r\n", StringComparison.Ordinal);
			string Error = i > 0 ? Message[7..i] : Message[7..];

			return "Unable to log in: " + Error;
		}

		private static bool IsChecked(object? Value)
		{
			return Value switch
			{
				bool b => b,
				string s => s == "on" || s == "true" || s == "1",
				_ => false
			};
		}
	}
}
