﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Uno.Wasm.Bootstrap.Extensions;

namespace Uno.Wasm.Bootstrap
{
	public class UnoInstallSDKTask_v0 : Microsoft.Build.Utilities.Task
	{
		public string MonoWasmSDKUri { get; set; }

		public string MonoWasmAOTSDKUri { get; set; }

		public string MonoTempFolder { get; set; }

		[Required]
		public string PackagerOverrideFolderPath { get; set; }

		[Required]
		public bool IsOSUnixLike { get; set; }

		[Microsoft.Build.Framework.Required]
		public string MonoRuntimeExecutionMode { get; set; }

		[Microsoft.Build.Framework.Required]
		public Microsoft.Build.Framework.ITaskItem[] Assets { get; set; }

		[Output]
		public string SdkPath { get; set; }

		[Output]
		public string PackagerBinPath { get; set; }

		[Output]
		public string PackagerProjectFile { get; private set; }

		public override bool Execute()
		{
			InstallSdk();

			return true;
		}

		private void InstallSdk()
		{
			var runtimeExecutionMode = ParseRuntimeExecutionMode();

			var sdkUri = string.IsNullOrWhiteSpace(MonoWasmSDKUri) ? Constants.DefaultSdkUrl : MonoWasmSDKUri;
			var aotUri = string.IsNullOrWhiteSpace(MonoWasmAOTSDKUri) ? Constants.DefaultAotSDKUrl : MonoWasmAOTSDKUri;

			var m = Regex.Match(sdkUri, @"(?!.*\-)(.*?)\.zip$");

			if (!m.Success)
			{
				throw new InvalidDataException($"Unable to find SHA in {sdkUri}");
			}

			var buildHash = m.Groups[1].Value;

			try
			{
				var sdkName = Path.GetFileNameWithoutExtension(new Uri(sdkUri).AbsolutePath.Replace('/', Path.DirectorySeparatorChar));

				Log.LogMessage("SDK: " + sdkName);
				SdkPath = Path.Combine(GetMonoTempPath(), sdkName);
				Log.LogMessage("SDK Path: " + SdkPath);

				if (
					Directory.Exists(SdkPath)
					&& !Directory.Exists(Path.Combine(SdkPath, "wasm-bcl", "wasm"))
				)
				{
					// The temp folder may get cleaned-up by windows' storage sense.
					Log.LogMessage($"Removing invalid mono-wasm SDK: {SdkPath}");
					Directory.Delete(SdkPath, true);
				}

				if (!Directory.Exists(SdkPath))
				{
					var zipPath = SdkPath + ".zip";
					Log.LogMessage($"Using mono-wasm SDK {sdkUri}");

					zipPath = RetreiveSDKFile(sdkName, sdkUri, zipPath);

					ZipFile.ExtractToDirectory(zipPath, SdkPath);
					Log.LogMessage($"Extracted {sdkName} to {SdkPath}");
				}

				if (
					(
					runtimeExecutionMode == RuntimeExecutionMode.FullAOT
					|| runtimeExecutionMode == RuntimeExecutionMode.InterpreterAndAOT
					|| HasBitcodeAssets()
					)
					&& !Directory.Exists(Path.Combine(SdkPath, "wasm-cross-release"))
				)
				{
					var aotZipPath = SdkPath + ".aot.zip";
					Log.LogMessage(Microsoft.Build.Framework.MessageImportance.High, $"Downloading {aotUri} to {aotZipPath}");
					aotZipPath = RetreiveSDKFile(sdkName, aotUri, aotZipPath);

					foreach (var entry in ZipFile.OpenRead(aotZipPath).Entries)
					{
						entry.ExtractRelativeToDirectory(SdkPath, true);
					}

					Log.LogMessage($"Extracted AOT {sdkName} to {SdkPath}");

					if (IsOSUnixLike)
					{
						Process.Start("chmod", $"-R +x {SdkPath}");
					}
				}

				if (!string.IsNullOrEmpty(PackagerOverrideFolderPath))
				{
					PackagerBinPath = Path.Combine(SdkPath, "packager2.exe");

					foreach (var file in Directory.EnumerateFiles(PackagerOverrideFolderPath))
					{
						var destFileName = Path.Combine(SdkPath, Path.GetFileName(file));
						Log.LogMessage($"Copy packager override {file} to {destFileName}");
						File.Copy(file, destFileName, true);
					}
				}
			}
			catch (Exception e)
			{
				throw new InvalidOperationException($"Failed to download the mono-wasm SDK at {sdkUri}, {e}");
			}
		}

		private bool HasBitcodeAssets()
			=> Assets.Any(a => a.ItemSpec.EndsWith(".bc", StringComparison.OrdinalIgnoreCase));

		private string RetreiveSDKFile(string sdkName, string sdkUri, string zipPath)
		{
			var tries = 3;

			while (--tries > 0)
			{
				try
				{
					var uri = new Uri(sdkUri);

					if (!uri.IsFile)
					{
						var client = new WebClient();
						var wp = WebRequest.DefaultWebProxy;
						wp.Credentials = CredentialCache.DefaultCredentials;
						client.Proxy = wp;

						Log.LogMessage(Microsoft.Build.Framework.MessageImportance.High, $"Downloading {sdkName} to {zipPath}");
						client.DownloadFile(sdkUri, zipPath);

						return zipPath;
					}
					else
					{
						return uri.LocalPath;
					}
				}
				catch(Exception e)
				{
					Log.LogWarning($"Failed to download Downloading {sdkName} to {zipPath}. Retrying... ({e.Message})");
				}
			}

			throw new Exception($"Failed to download {sdkName} to {zipPath}");
		}

		private string GetMonoTempPath()
		{
			var path = string.IsNullOrWhiteSpace(MonoTempFolder) ? Path.GetTempPath() : MonoTempFolder;

			Directory.CreateDirectory(path);

			return path;
		}

		private RuntimeExecutionMode ParseRuntimeExecutionMode()
		{
			if (Enum.TryParse<RuntimeExecutionMode>(MonoRuntimeExecutionMode, out var runtimeExecutionMode))
			{
				Log.LogMessage(MessageImportance.Low, $"MonoRuntimeExecutionMode={MonoRuntimeExecutionMode}");
			}
			else
			{
				throw new NotSupportedException($"The MonoRuntimeExecutionMode {MonoRuntimeExecutionMode} is not supported");
			}

			return runtimeExecutionMode;
		}
	}
}
