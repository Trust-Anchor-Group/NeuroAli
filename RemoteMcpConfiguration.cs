using System.Security.Cryptography;
using System.Text;
using Waher.Persistence;
using Waher.Persistence.Attributes;
using Waher.Persistence.Filters;

namespace NeuroAli
{
	/// <summary>
	/// A remote MCP endpoint saved through the configuration page.
	/// </summary>
	[CollectionName("NeuroAliRemoteMcpServers")]
	[TypeName(TypeNameSerialization.None)]
	[Index("Alias")]
	public class RemoteMcpServerDefinition
	{
		/// <summary>
		/// A remote MCP endpoint saved through the configuration page.
		/// </summary>
		public RemoteMcpServerDefinition()
		{
		}

		/// <summary>
		/// Object ID in the database.
		/// </summary>
		[ObjectId]
		public string? ObjectId { get; set; }

		/// <summary>
		/// Alias of the remote endpoint.
		/// </summary>
		public string Alias { get; set; } = string.Empty;

		/// <summary>
		/// URL of the remote MCP endpoint.
		/// </summary>
		public string Url { get; set; } = string.Empty;

		/// <summary>
		/// JWT issuer. Empty means the host name of the URL.
		/// </summary>
		[DefaultValueStringEmpty]
		public string Issuer { get; set; } = string.Empty;

		/// <summary>
		/// When the definition was saved.
		/// </summary>
		public DateTime Created { get; set; }

		/// <summary>
		/// If the endpoint is excluded from the proxy.
		/// </summary>
		public bool Disabled { get; set; }

		/// <summary>
		/// Tools seen during Neuron discovery, or null if the endpoint was added manually.
		/// </summary>
		[DefaultValueNull]
		public string[]? DiscoveredToolNames { get; set; }
	}

	/// <summary>
	/// Where a remote MCP endpoint is defined.
	/// </summary>
	public enum RemoteMcpSource
	{
		/// <summary>
		/// Defined using -r on the command line.
		/// </summary>
		CommandLine,

		/// <summary>
		/// Saved through the configuration page.
		/// </summary>
		Saved
	}

	/// <summary>
	/// Status of a remote MCP endpoint, as shown on the configuration page.
	/// </summary>
	/// <param name="Alias">Alias.</param>
	/// <param name="Url">URL.</param>
	/// <param name="Issuer">JWT issuer.</param>
	/// <param name="Source">Where the endpoint is defined.</param>
	/// <param name="Running">If the endpoint is used by the running process.</param>
	/// <param name="State">Human-readable state.</param>
	/// <param name="LastError">Last error, if any.</param>
	/// <param name="ToolNames">Tools from the latest successful discovery, if any.</param>
	/// <param name="CanToggle">If the enabled state can be changed from settings.</param>
	public sealed record RemoteMcpStatus(string Alias, string Url, string Issuer, RemoteMcpSource Source,
		bool Running, string State, string LastError, string[]? ToolNames, bool CanToggle);

	/// <summary>
	/// Saved remote MCP endpoints. Changes are applied when Neuro-Ali restarts.
	/// </summary>
	public static class RemoteMcpConfiguration
	{
		private static readonly SemaphoreSlim saveLock = new(1, 1);
		private static RemoteMcpEndpoint[] commandLine = [];
		private static RemoteMcpProxy? proxy = null;

		/// <summary>
		/// Gets saved definitions.
		/// </summary>
		public static async Task<RemoteMcpServerDefinition[]> GetSavedAsync()
		{
			IEnumerable<RemoteMcpServerDefinition> Items =
				await Database.Find<RemoteMcpServerDefinition>("Alias");

			return [.. Items];
		}

		/// <summary>
		/// Saves a new remote endpoint. It is used after Neuro-Ali restarts.
		/// </summary>
		/// <param name="Alias">Alias.</param>
		/// <param name="Url">URL of the remote MCP endpoint.</param>
		/// <param name="Issuer">JWT issuer. If empty, use the URL host for non-loopback endpoints.</param>
		/// <returns>Saved definition.</returns>
		/// <exception cref="Exception">If the definition is invalid, or the alias is in use.</exception>
		public static async Task<RemoteMcpServerDefinition> AddAsync(string Alias, string Url, string? Issuer)
		{
			Alias = Alias?.Trim() ?? string.Empty;
			Url = Url?.Trim() ?? string.Empty;
			Issuer = Issuer?.Trim() ?? string.Empty;

			RemoteMcpEndpoint.Create(Alias, Url, Issuer);   // Validates

			await saveLock.WaitAsync();
			try
			{
				if (await FindAsync(Alias) is not null)
					throw new Exception("A saved remote server with alias '" + Alias + "' already exists.");

				if (Array.Exists(commandLine, E => string.Equals(E.Alias, Alias, StringComparison.OrdinalIgnoreCase)))
					throw new Exception("Alias '" + Alias + "' is already defined on the command line.");

				RemoteMcpServerDefinition Definition = new()
				{
					Alias = Alias,
					Url = Url,
					Issuer = Issuer,
					Created = DateTime.UtcNow
				};

				await Database.Insert(Definition);
				return Definition;
			}
			finally
			{
				saveLock.Release();
			}
		}

		/// <summary>
		/// Removes a saved remote endpoint and stops proxying it.
		/// </summary>
		/// <param name="Alias">Alias.</param>
		/// <returns>If a saved definition was removed.</returns>
		public static async Task<bool> RemoveAsync(string Alias)
		{
			await saveLock.WaitAsync();
			try
			{
				RemoteMcpServerDefinition? Definition = await FindAsync(Alias);
				if (Definition is null)
					return false;

				await Database.Delete(Definition);
				if (proxy is RemoteMcpProxy Proxy && !IsCommandLine(Alias))
					await Proxy.DisableAsync(Definition.Alias);

				return true;
			}
			finally
			{
				saveLock.Release();
			}
		}

		/// <summary>
		/// Discovers endpoints using the linked account and saves new endpoints disabled.
		/// </summary>
		public static async Task<int> DiscoverAsync(string BaseUrl, string? Issuer)
		{
			RemoteMcpEndpoint Host = RemoteMcpEndpoint.Create("discovery", BaseUrl.Trim(), Issuer);
			if (Host.Url.AbsolutePath != "/" || Host.Url.UserInfo.Length > 0 ||
				Host.Url.Query.Length > 0 || Host.Url.Fragment.Length > 0)
			{
				throw new Exception("Enter the Neuron origin, for example https://neuron.example.");
			}

			RemoteMcpProxy Proxy = proxy ?? throw new Exception("The remote proxy is unavailable.");
			(Uri Url, string[] ToolNames)[] Endpoints = await Proxy.DiscoverAsync(Host);

			await saveLock.WaitAsync();
			try
			{
				RemoteMcpServerDefinition[] Saved = await GetSavedAsync();
				HashSet<string> Aliases = new HashSet<string>(Saved.Select(Definition => Definition.Alias),
					StringComparer.OrdinalIgnoreCase);
				Aliases.UnionWith(commandLine.Select(Endpoint => Endpoint.Alias));

				foreach ((Uri Url, string[] ToolNames) in Endpoints)
				{
					if (Array.Exists(commandLine, Endpoint => Endpoint.Url == Url &&
						string.Equals(Endpoint.Issuer, Host.Issuer, StringComparison.OrdinalIgnoreCase)))
					{
						continue;
					}

					RemoteMcpServerDefinition? Existing = Array.Find(Saved, Definition =>
						Uri.TryCreate(Definition.Url, UriKind.Absolute, out Uri? SavedUrl) && SavedUrl == Url &&
						string.Equals(RemoteMcpEndpoint.ResolveIssuer(Url, Definition.Issuer),
							Host.Issuer, StringComparison.OrdinalIgnoreCase));

					if (Existing is not null)
					{
						Existing.DiscoveredToolNames = ToolNames;
						await Database.Update(Existing);
						continue;
					}

					string Alias = CreateAlias(Url, Host.Issuer, Aliases);
					RemoteMcpServerDefinition Definition = new RemoteMcpServerDefinition
					{
						Alias = Alias,
						Url = Url.AbsoluteUri,
						Issuer = Host.Issuer,
						Created = DateTime.UtcNow,
						Disabled = true,
						DiscoveredToolNames = ToolNames
					};

					await Database.Insert(Definition);
				}
			}
			finally
			{
				saveLock.Release();
			}

			return Endpoints.Length;
		}

		private static string CreateAlias(Uri Url, string Issuer, HashSet<string> Aliases)
		{
			string Path = Url.AbsolutePath.TrimEnd('/');
			string Name = new string(Path[(Path.LastIndexOf('/') + 1)..].ToLowerInvariant()
				.Where(char.IsAsciiLetterOrDigit).Take(9).ToArray());
			if (Name.Length == 0)
				Name = "mcp";

			for (int Attempt = 0; ; Attempt++)
			{
				byte[] Hash = SHA256.HashData(Encoding.UTF8.GetBytes(Url.AbsoluteUri + "\n" + Issuer + "\n" + Attempt));
				string Alias = Name + "-" + Convert.ToHexString(Hash.AsSpan(0, 3)).ToLowerInvariant();
				if (Aliases.Add(Alias))
					return Alias;
			}
		}

		/// <summary>
		/// Saves and applies the enabled state of a saved endpoint.
		/// </summary>
		public static async Task SetEnabledAsync(string Alias, bool Enabled)
		{
			await saveLock.WaitAsync();
			try
			{
				RemoteMcpServerDefinition? Definition = await FindAsync(Alias);
				if (Definition is null || IsCommandLine(Alias))
					throw new Exception("No configurable remote server was found for that alias.");

				RemoteMcpProxy Proxy = proxy ?? throw new Exception("The remote proxy is unavailable.");
				RemoteMcpEndpoint Endpoint = RemoteMcpEndpoint.Create(Definition.Alias, Definition.Url, Definition.Issuer);
				if (Definition.Disabled == Enabled)
				{
					Definition.Disabled = !Enabled;
					await Database.Update(Definition);
				}

				if (Enabled)
					Proxy.Enable(Endpoint);
				else
					await Proxy.DisableAsync(Definition.Alias);
			}
			finally
			{
				saveLock.Release();
			}
		}

		private static bool IsCommandLine(string Alias) =>
			Array.Exists(commandLine, Endpoint => string.Equals(Endpoint.Alias, Alias, StringComparison.OrdinalIgnoreCase));

		private static async Task<RemoteMcpServerDefinition?> FindAsync(string Alias)
		{
			foreach (RemoteMcpServerDefinition Definition in await Database.Find<RemoteMcpServerDefinition>(
				new FilterFieldEqualTo("Alias", Alias)))
			{
				return Definition;
			}

			// Aliases are case-insensitive.
			foreach (RemoteMcpServerDefinition Definition in await GetSavedAsync())
			{
				if (string.Equals(Definition.Alias, Alias, StringComparison.OrdinalIgnoreCase))
					return Definition;
			}

			return null;
		}

		/// <summary>
		/// Loads the endpoints to use at startup: command-line definitions, followed by saved
		/// definitions. Command-line definitions take precedence if aliases collide.
		/// </summary>
		/// <param name="CommandLine">Endpoints defined on the command line.</param>
		/// <param name="Warnings">Receives warnings about saved definitions that were skipped.</param>
		/// <returns>Endpoints to use.</returns>
		internal static async Task<List<RemoteMcpEndpoint>> LoadAsync(IEnumerable<RemoteMcpEndpoint> CommandLine,
			List<string> Warnings)
		{
			commandLine = [.. CommandLine];

			List<RemoteMcpEndpoint> Result = [.. commandLine];

			foreach (RemoteMcpServerDefinition Definition in await GetSavedAsync())
			{
				if (Definition.Disabled)
					continue;

				if (Result.Exists(E => string.Equals(E.Alias, Definition.Alias, StringComparison.OrdinalIgnoreCase)))
				{
					Warnings.Add("Saved remote '" + Definition.Alias + "' is ignored, since the alias is defined on the command line.");
					continue;
				}

				try
				{
					RemoteMcpEndpoint Endpoint = RemoteMcpEndpoint.Create(Definition.Alias, Definition.Url, Definition.Issuer);
					Result.Add(Endpoint);
				}
				catch (Exception ex)
				{
					Warnings.Add("Saved remote '" + Definition.Alias + "' is ignored: " + ex.Message);
				}
			}

			return Result;
		}

		/// <summary>
		/// Registers the running proxy, so its state can be shown.
		/// </summary>
		internal static void SetProxy(RemoteMcpProxy? Proxy)
		{
			proxy = Proxy;
		}

		/// <summary>
		/// Gets the status of all remote endpoints: those used by the running process, and
		/// saved changes that take effect after a restart.
		/// </summary>
		public static async Task<RemoteMcpStatus[]> GetStatusAsync()
		{
			List<RemoteMcpStatus> Result = [];
			RemoteMcpServerDefinition[] Saved = await GetSavedAsync();

			foreach (RemoteMcpEndpoint Endpoint in commandLine)
			{
				RemoteMcpServerState? ServerState = proxy?.GetState(Endpoint.Alias);
				Result.Add(new RemoteMcpStatus(Endpoint.Alias, Endpoint.Url.ToString(), Endpoint.Issuer,
					RemoteMcpSource.CommandLine, true, ServerState?.State ?? "Running",
					ServerState?.LastError ?? string.Empty, ServerState?.ToolNames, false));
			}

			foreach (RemoteMcpServerDefinition Definition in Saved)
			{
				if (IsCommandLine(Definition.Alias))
					continue;

				string Issuer = Definition.Issuer;
				if (Uri.TryCreate(Definition.Url, UriKind.Absolute, out Uri? Parsed))
					Issuer = RemoteMcpEndpoint.ResolveIssuer(Parsed, Issuer);

				RemoteMcpServerState? ServerState = proxy?.GetState(Definition.Alias);
				string State = ServerState?.State ?? (Definition.Disabled ? "Disabled" : "Added, used after restart");
				bool CanToggle = ServerState is not null || Definition.Disabled;
				Result.Add(new RemoteMcpStatus(Definition.Alias, Definition.Url, Issuer,
					RemoteMcpSource.Saved, ServerState is not null, State, ServerState?.LastError ?? string.Empty,
					ServerState?.ToolNames ?? Definition.DiscoveredToolNames, CanToggle));
			}

			return [.. Result];
		}
	}
}
