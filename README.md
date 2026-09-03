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
| `-h`        | Shows command-line parameters available.                         |

STDIO interface
------------------

Neuro-Ali supports the MCP STDIO interface. This means you can connect to the application
by selecting STDIO in your LLM and pointing to the Neuro-Ali executable file, optionally
providing command-line arguments. The application will then read and write to standard
input and output streams. Input is always JSON-RPC calls to the MCP server, and output is
either JSON-RPC responses, or SSE events (containing JSON-RPC encoded events and optional
comments).

You quit the application by entering an empty line.

HTTP interface
-----------------

When the console application starts, it starts a local HTTP server on the specified port 
(default is 8080). The HTTP server provides multiple resources to connect to. The main
resource is the `/MCP` endpoint, which provides the same functionality as the STDIO 
interface, but over streaming HTTP. The local HTTP server also provides individual endpoints,
for individual MCP servers, if you want to connect and test them individually, or as groups.
The following table lists the available endpoints:

| Resource        | Description                                                                                                          |
|:----------------|:---------------------------------------------------------------------------------------------------------------------|
| `/MCP`          | Main MCP endpoint, provides the same functionality as STDIO interface, and contains all available MCP functionality. |
| `/MCP/Content`  | Provides access to Internet Content.                                                                                 |
| `/MCP/EventLog` | Allows the MCP client to log events, and search event logs.                                                          |
| `/MCP/Files`    | Provides a means for MCP clients to store and access files in local File Storage.                                    |
| `/MCP/XMPP`     | Permits an MCP client to connect to, and interact on, the XMPP network.                                              |
| `/MCP/Identity` | Enables MCP clients to apply for and use digital Legal Identities.                                                   |
| `/MCP/Payments` | Gives MCP clients capabilities to make payments.                                                                     |

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
