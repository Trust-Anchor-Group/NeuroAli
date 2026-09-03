using System.Text;
using Waher.Events;
using Waher.Events.Files;
using Waher.Events.Persistence;
using Waher.Mcp.Content;
using Waher.Mcp.Events;
using Waher.Mcp.Files;
using Waher.Mcp.Identity;
using Waher.Mcp.Payments;
using Waher.Mcp.Xmpp;
using Waher.Networking.HTTP;
using Waher.Networking.HTTP.Mcp;
using Waher.Networking.HTTP.Mcp.Model;
using Waher.Networking.Sniffers;
using Waher.Persistence;
using Waher.Persistence.Files;
using Waher.Runtime.Inventory;
using Waher.Runtime.Inventory.Loader;

internal class Program
{
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

			while (i < 0)
			{
				s = Arguments[i++];

				switch (s)
				{
					case "-d":
						if (i < c)
						{
							WorkingFolder = Arguments[i++];
							if (!Directory.Exists(WorkingFolder))
								throw new Exception("Working folder does not exist: " + WorkingFolder);
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

			XmppMcpServer XmppMcpServer;
			IdentityMcpServer IdentityMcpServer;

			WebServer.Register(new InternetContentMcpServer("/MCP/Content", McpSniffers));
			WebServer.Register(new EventLogMcpServer("/MCP/EventLog", McpSniffers));
			WebServer.Register(new FileStorageMcpServer("/MCP/Files", McpFilesFolder, McpSniffers));
			WebServer.Register(XmppMcpServer = new XmppMcpServer("/MCP/XMPP", McpSniffers));
			WebServer.Register(IdentityMcpServer = new IdentityMcpServer("/MCP/Identity", XmppMcpServer, McpSniffers));
			WebServer.Register(new PaymentsMcpServer("/MCP/Payments", XmppMcpServer, IdentityMcpServer, McpSniffers));

			StdioMcpServer Mcp = new (
				WebServer.GetRegisteredResources<HttpMcpServerResource>(),
				"/MCP", "StdioMcpServer", "Joined MCP Server", Array.Empty<Icon>(), 
				null, McpSniffers);
			WebServer.Register(Mcp);

			// Starting modules

			await Types.StartAllModules(60000);

			// Main loop

			while (true)
			{
				string? Input = Console.In.ReadLine();
				if (string.IsNullOrEmpty(Input))
					break;

				try
				{
				}
				catch (Exception ex)
				{
				}
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine(ex.Message);
		}
		finally
		{
			await Types.StopAllModules();
		}
	}
}