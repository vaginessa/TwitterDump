﻿using System.Diagnostics;
using System.Text;
using System.Linq;
using System.Text.Json;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace TwitterDump
{
	public static class Program
	{
		public static readonly string[] LineSeparators = new string[3] { "\r\n", "\r", "\n" };
		private static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true
		};

		public static Config TheConfig
		{
			get; private set;
		} = null!;

		public static void Main(string[] args) => MainAsync(args).Wait();

		private static bool HasCmdSwitch(string[] args, string switchStr)
		{
			return args.Any(arg => arg.StartsWith("-" + switchStr, StringComparison.OrdinalIgnoreCase) || arg.StartsWith("/" + switchStr, StringComparison.OrdinalIgnoreCase));
		}

		private static string GetCmdSwitch(string[] args, string switchStr, string defaultValue)
		{
			return (from arg in args where arg.StartsWith("-" + switchStr, StringComparison.OrdinalIgnoreCase) || arg.StartsWith("/" + switchStr, StringComparison.OrdinalIgnoreCase) select arg[(switchStr.Length + 1)..]).FirstOrDefault(defaultValue);
		}

		public static async Task MainAsync(string[] args)
		{
			TheConfig = new Config();

			Log.Logger = new LoggerConfiguration()
				.MinimumLevel.Debug()
				.WriteTo.File(GetCmdSwitch(args, "l", "TwitterDump.log"), buffered: true, flushToDiskInterval: TimeSpan.FromMilliseconds(100), fileSizeLimitBytes: 16777216 /* 128MB */, rollOnFileSizeLimit: true)
				.WriteTo.Console(theme: AnsiConsoleTheme.Code)
				.CreateLogger();

			var configFileName = GetCmdSwitch(args, "c", "TwitterDump.json");
			if (!File.Exists(configFileName) || HasCmdSwitch(args, "gc"))
			{
				try
				{
					Log.Information("Exporting default config file to {file}.", configFileName);
					await File.WriteAllTextAsync(configFileName, JsonSerializer.Serialize(TheConfig, JsonOptions));
				}
				catch (Exception e)
				{
					Log.Error(e, "An exception occurred during exporting default config file.");
				}

				return;
			}

			Log.Information("Using configuration file {file}.", configFileName);
			try
			{
				string data = await File.ReadAllTextAsync(configFileName);
				Config? parsed = JsonSerializer.Deserialize<Config>(data, JsonOptions);
				if (parsed == null)
					Log.Warning("Json deserializer returned null! Using default config instead.");
				else
					TheConfig = parsed;
			}
			catch (Exception e)
			{
				Log.Error(e, "An exception occurred during config parse.");
			}

			string inputFileName = TheConfig.ListFile;
			if (CheckFileExistence(TheConfig.Retriever.Executable, "Gallery-DL executable") || CheckFileExistence(TheConfig.Downloader.Executable, "Aria2 Executable") || CheckFileExistence(inputFileName, "Member ID list"))
				return;

			Log.Information("Reading list file {file}.", inputFileName);
			string[] _targets = File.ReadAllLines(inputFileName);
			IEnumerable<Target> targets = from target in _targets where !string.IsNullOrWhiteSpace(target) select new Target(target);

			var useRetrieverAsDownloader = TheConfig.UseRetrieverIntegratedDownloader;

			var tasks = new List<Task>();
			using var retrieverParallellismLimiter = new SemaphoreSlim(TheConfig.Parallelism.RetrieverParallelism);
			using var downloaderParallellismLimiter = new SemaphoreSlim(TheConfig.Parallelism.DownloaderParallelism);
			foreach (Target target in targets)
			{
				tasks.Add(Task.Run(async () =>
				{
					List<string>? downloadListLines = await RetrieveTask(TheConfig, target, retrieverParallellismLimiter, useRetrieverAsDownloader);
					if (downloadListLines != null)
						await DownloadTask(TheConfig, target, downloadListLines, downloaderParallellismLimiter);
				}));
			}

			await Task.WhenAll(tasks);

			Log.Information("Finished all jobs. Exiting...");
		}

		private static async Task DownloadTask(Config config, Target target, List<string> aria2InputLines, SemaphoreSlim downloaderParallellismLimiter)
		{
			Log.Debug("Waiting for downloader parallellism semaphore...");
			await downloaderParallellismLimiter.WaitAsync();

			// Enqueue download task
			Log.Information("Now downloading: {id}.", target.ID);
			try
			{
				await Download(config, target, aria2InputLines);
				Log.Information("Successfully downloaded: {id}.", target.ID);
			}
			finally
			{
				downloaderParallellismLimiter.Release();
			}
		}

		private static async Task<List<string>?> RetrieveTask(Config config, Target target, SemaphoreSlim semaphore, bool useRetrieverAsDownloader)
		{
			await semaphore.WaitAsync();

			try
			{
				Log.Information("Now retrieving: {id}", target.ID);

				// Retrieve media CDN URL.
				var retrievedResult = await Retrieve(config, target);

				if (!useRetrieverAsDownloader)
				{
					Log.Information("Successfully retrieved: {id}.", target.ID);

					Log.Information("Building download list: {id}.", target.ID);
					List<string> result = MakeDownloadList(target, retrievedResult!);
					Log.Information("Successfully built download list: {id}.", target.ID);
					return result;
				}
			}
			finally
			{
				semaphore.Release();
			}

			return null;
		}

		private static List<string> MakeDownloadList(Target target, string retrievedResult)
		{
			string[] retrievedURLs = retrievedResult.Split(LineSeparators, StringSplitOptions.TrimEntries);
			int urlCount = retrievedURLs.Length;
			var mainURLs = new List<(string, string?)>(urlCount);
			var mirrorURLs = new Dictionary<string, List<string>>();
			string? lastMainURL = null;
			var duplicateCheckSet = new HashSet<string>();
			foreach (string url in retrievedURLs)
			{
				if (url.StartsWith("http", StringComparison.InvariantCultureIgnoreCase))
				{
					lastMainURL = url.Trim();
					string? newFileName = target.protocol.NewFileNameRetriever(url);
					string fileName = newFileName ?? url.ExtractFileName();

					if (TheConfig.SkipAlreadyExists)
					{
						string path = Path.Combine(TheConfig.Destination.FormatDestination(target.ID), fileName);
						if (new FileInfo(path).Exists)
						{
							Log.Warning("File {file} already exists! Skipping media {media} of member {member}.", path, fileName, target.ID);
							continue;
						}
					}

					if (duplicateCheckSet.Contains(fileName))
					{
						Log.Warning("Skipped {file} as it's already processed previously.", fileName);
						continue;
					}
					else
					{
						mainURLs.Add((lastMainURL, newFileName));
					}

					duplicateCheckSet.Add(fileName);
				}
				else if (lastMainURL != null && url.StartsWith('|'))
				{
					if (!mirrorURLs.ContainsKey(lastMainURL))
						mirrorURLs[lastMainURL] = new List<string>();
					mirrorURLs[lastMainURL].Add(url[1..].Trim());
				}
			}

			var aria2InputLines = new List<string>(urlCount);
			foreach ((string mainURL, string? newFileName) in mainURLs)
			{
				var builder = new StringBuilder();
				builder.Append(mainURL);
				if (mirrorURLs.ContainsKey(mainURL))
				{
					builder.Append('\t');
					builder.AppendJoin('\t', mirrorURLs[mainURL]);
				}
				aria2InputLines.Add(builder.ToString());
				if (newFileName != null)
					aria2InputLines.Add($"  out={newFileName}");
			}

			return aria2InputLines;
		}

		private static async Task<string?> Retrieve(Config config, Target target)
		{
			var gallery_dl = new Process();
			gallery_dl.StartInfo.FileName = config.Retriever.Executable;
			gallery_dl.StartInfo.Arguments = config.Retriever.Parameters.FormatRetriverParameters(target.ID);
			gallery_dl.StartInfo.UseShellExecute = false;
			gallery_dl.StartInfo.RedirectStandardOutput = true;

			gallery_dl.Start();

			if (config.UseRetrieverIntegratedDownloader)
			{
				await gallery_dl.WaitForExitAsync();
				return null;
			}
			else
			{
				using var stream = new MemoryStream();
				await gallery_dl.StandardOutput.BaseStream.CopyToAsync(stream, 4096);
				await gallery_dl.WaitForExitAsync();
				return Encoding.UTF8.GetString(stream.ToArray());
			}
		}

		private static async Task Download(Config config, Target target, List<string> input)
		{
			// Workaround
			string tmpFileName = $"{target.ID.ToFileName()}.{Random.Shared.NextInt64()}";
			await File.WriteAllLinesAsync(tmpFileName, input);

			var aria2 = new Process();
			aria2.StartInfo.FileName = config.Downloader.Executable;
			aria2.StartInfo.Arguments = FormatDownloaderParameters(config.Downloader.Parameters, target.ID, tmpFileName, config.Destination);
			aria2.StartInfo.UseShellExecute = true;
			aria2.Start();
			await aria2.WaitForExitAsync();
			File.Delete(tmpFileName);
		}

		private static bool CheckFileExistence(string fileName, string description)
		{
			if (!File.Exists(fileName))
			{
				Log.Fatal("File {file} ({desc}) not found! The program will exit.", fileName, description);
				return true;
			}
			return false;
		}

		public static string FormatRetriverParameters(this string format, string memberID) => format.FormatTokens(new Dictionary<string, string>
		{
			["memberID"] = memberID,
			["memberIDFileName"] = memberID.ToFileName()
		});

		public static string FormatDownloaderParameters(this string format, string memberID, string inputFileName, string destinationFormat) => format.FormatTokens(new Dictionary<string, string>
		{
			["memberID"] = memberID,
			["memberIDFileName"] = memberID.ToFileName(),
			["inputFileName"] = inputFileName,
			["destination"] = destinationFormat.FormatDestination(memberID)
		});

		public static string FormatDestination(this string format, string memberID) => format.FormatTokens(new Dictionary<string, string>
		{
			["memberID"] = memberID,
			["memberIDFileName"] = memberID.ToFileName()
		});

		private static string FormatTokens(this string format, IDictionary<string, string> tokens)
		{
			foreach (KeyValuePair<string, string> token in tokens)
				format = format.Replace($"${{{token.Key}}}", token.Value, StringComparison.OrdinalIgnoreCase);
			return format;
		}
	}
}
