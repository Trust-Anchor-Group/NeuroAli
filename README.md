Neuro-Ali
===========

Neuro-Ali is a STDIO MCP Server for TAG Neuron services, including XMPP-communication, 
Event logs, File storage, Legal Identities, Smart Contracts and Payments. Neuro-Ali also
sets up a local web server that can be used, if the LLM agent does not support STDIO.

Command-line options
-----------------------

The executable supports the following command-line options:

| Option      | Description                                                      |
|:------------|:-----------------------------------------------------------------|
| `-d FOLDER` | Sets the working folder where data is stored to `FOLDER`.        |
| `-p PORT`   | Defines the HTTP port number to use as `PORT`. (Default is 8080) |
| `-y`        | Uses the working folder even if it contains unrelated files.     |
| `-r ALIAS URL` | Proxies the remote MCP endpoint at `URL` (see [Remote MCP servers](#remote-mcp-servers)). Can be repeated. |
| `-ri ALIAS JID` | Requests JWT tokens for remote `ALIAS` from `JID`, instead of the host name of its URL. |
| `-h`        | Shows command-line parameters available.                         |

If `-d` is not given, the working folder is the current directory (where the program is
run from), not the folder the program is installed in.

On startup, Neuro-Ali checks that the working folder is empty or only contains folders it
created itself (`Data`, `Events`, `HTTP`, `JSON-RPC`, `MCP`). If it contains anything else,
Neuro-Ali shows the full path and the unrelated items on standard error. When run from a
terminal, it asks for confirmation before continuing. When input is redirected, for example
when an MCP client starts it, it cannot ask, so it exits without starting. Use `-d` to point
to a dedicated folder, or `-y` to skip the check.

STDIO interface
------------------

Neuro-Ali supports the MCP STDIO interface. This means you can connect to the application
by selecting STDIO in your LLM and pointing to the Neuro-Ali executable file, optionally
providing command-line arguments. The application will then read and write to standard
input and output streams. Input is always JSON-RPC calls to the MCP server, and output is
either JSON-RPC responses, or SSE events (containing JSON-RPC encoded events and optional
comments).

You quit the application by entering an empty line.

Remote MCP servers
---------------------

Neuro-Ali can proxy MCP endpoints hosted on other Neurons, so their tools appear next to
the local ones in the STDIO interface:

```
NeuroAli -d C:\NeuroAli -r b https://neuron-b.example/MCP/EventLog
```

Authentication uses the XMPP account Neuro-Ali is logged in with (the account linked
through the local XMPP tools). For each remote, Neuro-Ali requests a short-lived JWT
over XMPP (`urn:xmpp:jwt:0`) from the remote Neuron's domain, and sends it as
`Authorization: Bearer` over HTTPS. For this to work, the remote Neuron must have a user
named exactly by Neuro-Ali's bare JID, with the privileges the remote tools require.
Tokens are cached and renewed 30 seconds before they expire.

To proxy a Neuron running on the same machine, use its loopback endpoint, for example
`-r local-eventlog http://localhost/MCP/EventLog`. With no explicit issuer, Neuro-Ali
requests the JWT from the connected local XMPP broker. Set up an account on that broker
and grant it the privileges for the tools you want to use.

* Remote tools and prompts are listed as `ALIAS_NAME`, with `[ALIAS]` added to their
  descriptions. Remote resources keep their URIs.
* If no XMPP account is linked yet, remote items are left out of the lists. Calling a
  remote tool asks for the XMPP login first. After the next tool call, Neuro-Ali tells
  the client that its lists have changed.
* Neuro-Ali keeps a server-sent event stream open to each remote, so remote servers
  can ask for input. Neuro-Ali only offers URL-mode input to remote servers, so
  credentials are always entered in the remote Neuron's own form.
* Remote URLs must use HTTPS (plain HTTP is only accepted for `localhost`), and
  redirects are not followed, so the token is never sent to another host.
* Calls to remote servers run in the background, so slow remote calls do not block
  local ones.

Administration pages
-----------------------

The local web server has administration pages under `/Admin`. They only answer requests
from the local machine to `localhost` (or a loopback address):

| Page                                   | Description                                                                                  |
|:---------------------------------------|:---------------------------------------------------------------------------------------------|
| `http://localhost:8080/Admin/Remotes.md` | Discover MCP servers from a Neuron base URL and enable or disable saved remote servers. |
| `http://localhost:8080/Admin/Sniffers.md` | Lists sniffer files (MCP, JSON-RPC and HTTP), and shows them as HTML.                        |

Discovery checks which servers offer tools, prompts, or resources to the linked XMPP account.
Newly discovered servers start disabled. Enable or disable them on the page to update the
proxy immediately. The setting is saved across restarts. Gateways that advertise exact MCP
paths can discover custom endpoints at any path. For older gateways, discovery checks the
standard paths and paths inferred from advertised MCP scopes. You can enter an exact URL
manually for other endpoints. Manual additions still take effect after restart. If an alias
is defined in both settings and the command line, the command line wins.

The pages are Markdown files with server-side script, in the `Root/Admin` folder next to the
executable, and can be edited there. Forms use a per-session anti-forgery token.

HTTP interface
-----------------

When the console application starts, it starts a local HTTP server on the specified port 
(default is 8080). The HTTP server provides multiple resources to connect to. The main
resource is the `/MCP` endpoint, which provides the same functionality as the STDIO 
interface, but over streaming HTTP. The local HTTP server also provides individual endpoints,
for individual MCP servers, if you want to connect and test them individually, or as groups.
The following table lists the available endpoints:

| Resource        | Description                                                                                                                    |
|:----------------|:-------------------------------------------------------------------------------------------------------------------------------|
| `/MCP`          | Main MCP endpoint, provides the same functionality as STDIO interface, and contains all available MCP functionality.           |
| `/MCP/Content`  | Provides access to Internet Content.                                                                                           |
| `/MCP/EventLog` | Allows the MCP client to log events, and search event logs.                                                                    |
| `/MCP/Files`    | Provides a means for MCP clients to store and access files in local File Storage.                                              |
| `/MCP/XMPP`     | Permits an MCP client to connect to, and interact on, the XMPP network.                                                        |
| `/MCP/Identity` | Enables MCP clients to apply for and use digital Legal Identities and create, sign and propose smart contracts from templates. |
| `/MCP/Payments` | Gives MCP clients capabilities to make payments.                                                                               |

Docker
---------

A docker file is provided to run Neuro-Ali in a container. It defines a volume for storing
data, and exposes the HTTP port.

Local Database
-----------------

A local encrypted database is created in the `Data` subfolder to the working folder. Any data
generated and persisted will be stored in this database. To ensure the data is not lost when
running in a container, it is recommended to mount a volume to the working folder, and make
regular backups if the data is important.

Local Event Log
------------------

Two event sinks are registered to the internal event log: The first stores events in the
local internal (and encrypted) database. Events are kept there fore 90 days. Events are also
stored in local files in the `Events` subfolder to the working folder. Files are kept there
7 days.

Sniffers
-----------

Sniffers are added for transparency at different levels of the communication stack. They
store files in different folders, and are kept for 7 days. Sniffer output are never 
transmitted anywhere, just stored locally for inspection, if you want to review the 
communication between the MCP client and the MCP server. The following sniffer folders
are available:

* `HTTP` subfolder contains sniffer HTTP communication.
* `JSON-RPC` subfolder contains JSON-RPC requests, responses and events.
* `MCP/Sniffers` subfolder contains MCP protocol messages, responses and events.

Local File Storage
---------------------

MCP Clients can store files in local file storage. These files are available in the 
`/MCP/Files` subfolder.
