namespace NeuroAli
{
	/// <summary>
	/// Checks the working folder before Neuro-Ali creates anything in it, so that
	/// running the program from the wrong folder by accident does not fill an
	/// unrelated folder (a project, a home folder, the install folder) with
	/// Neuro-Ali data.
	/// </summary>
	internal static class WorkingFolderCheck
	{
		/// <summary>
		/// Top-level folders Neuro-Ali creates in its working folder.
		/// </summary>
		private static readonly string[] NeuroAliFolders = ["Data", "Events", "HTTP", "JSON-RPC", "MCP"];

		/// <summary>
		/// Files the operating system may place in any folder. They do not count
		/// as unrelated content.
		/// </summary>
		private static readonly string[] IgnoredFiles = ["desktop.ini", "Thumbs.db", ".DS_Store"];

		private const int MaxListed = 10;

		/// <summary>
		/// Returns true if Neuro-Ali may use the folder: it is empty, contains only
		/// Neuro-Ali folders, or the user confirms. Messages go to stderr, since
		/// stdout carries the MCP protocol.
		/// </summary>
		/// <param name="WorkingFolder">Working folder, as given or defaulted.</param>
		/// <param name="FromArgument">If the folder was given with -d.</param>
		/// <param name="ExecutableFolder">Folder of the Neuro-Ali executable.</param>
		public static bool Confirm(string WorkingFolder, bool FromArgument, string ExecutableFolder)
		{
			string FullPath = Path.GetFullPath(WorkingFolder);
			if (!Directory.Exists(FullPath))
				return true;

			List<string> Unrelated = [];

			foreach (string Entry in Directory.EnumerateFileSystemEntries(FullPath))
			{
				string Name = Path.GetFileName(Entry);
				bool IsFolder = Directory.Exists(Entry);

				if (IsFolder && Array.IndexOf(NeuroAliFolders, Name) >= 0)
					continue;

				if (!IsFolder && Array.IndexOf(IgnoredFiles, Name) >= 0)
					continue;

				Unrelated.Add(IsFolder ? Name + Path.DirectorySeparatorChar : Name);
			}

			if (Unrelated.Count == 0)
				return true;

			Unrelated.Sort(StringComparer.OrdinalIgnoreCase);

			TextWriter Error = Console.Error;
			string FullExecutableFolder = Path.GetFullPath(ExecutableFolder);
			bool IsInstallFolder = string.Equals(
				Path.TrimEndingDirectorySeparator(FullPath),
				Path.TrimEndingDirectorySeparator(FullExecutableFolder),
				OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

			Error.WriteLine();
			Error.WriteLine("WARNING: The Neuro-Ali working folder already contains files that Neuro-Ali did not create.");
			Error.WriteLine();
			Error.WriteLine("  Working folder:  " + FullPath);
			Error.WriteLine("                   " + (FromArgument
				? "(given with -d)"
				: "(the current directory, because no -d option was given)"));

			if (IsInstallFolder)
				Error.WriteLine("                   This is the folder Neuro-Ali is installed in.");

			Error.WriteLine("  Executable:      " + FullExecutableFolder);
			Error.WriteLine("  Unrelated items: " + Unrelated.Count.ToString());

			for (int i = 0; i < Unrelated.Count && i < MaxListed; i++)
				Error.WriteLine("    " + Unrelated[i]);

			if (Unrelated.Count > MaxListed)
				Error.WriteLine("    ... and " + (Unrelated.Count - MaxListed).ToString() + " more");

			Error.WriteLine();
			Error.WriteLine("If you continue, Neuro-Ali creates the folders " + string.Join(", ", NeuroAliFolders));
			Error.WriteLine("here, and stores its encrypted database, logs and files in them.");
			Error.WriteLine("Existing files are not changed.");
			Error.WriteLine();
			Error.WriteLine("To use a separate folder instead, restart with:  -d <folder>");
			Error.WriteLine("To skip this check, restart with:                -y");
			Error.WriteLine();

			if (Console.IsInputRedirected)
			{
				// Launched by an MCP client: stdin carries the protocol, so nobody can answer.
				Error.WriteLine("Neuro-Ali was not started, since input is redirected (for example when an MCP");
				Error.WriteLine("client starts it) and the choice cannot be confirmed at a terminal.");
				Error.Flush();
				return false;
			}

			Error.Write("Continue in this folder? [y/N] ");
			Error.Flush();

			string? Answer = Console.ReadLine()?.Trim();
			bool Confirmed = string.Equals(Answer, "y", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(Answer, "yes", StringComparison.OrdinalIgnoreCase);

			if (!Confirmed)
				Error.WriteLine("Neuro-Ali was not started.");

			return Confirmed;
		}
	}
}
