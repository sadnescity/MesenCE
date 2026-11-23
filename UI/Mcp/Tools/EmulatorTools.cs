using Avalonia.Threading;
using Mesen.Config;
using Mesen.Debugger;
using Mesen.Interop;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;

namespace Mesen.Mcp.Tools
{
	[McpServerToolType]
	public class EmulatorTools
	{
		[McpServerTool(Name = "mesen_load_rom", ReadOnly = false, Destructive = false, OpenWorld = false),
		 Description("Load a ROM file into the emulator. Returns ROM info on success.")]
		public static string LoadRom(
			[Description("Absolute path to the ROM file")] string filepath,
			[Description("Optional path to an IPS/BPS patch file")] string? patchFile = null)
		{
			if(!File.Exists(filepath)) {
				throw new McpException("File not found: " + filepath);
			}

			bool result = EmuApi.LoadRom(filepath, patchFile ?? string.Empty);
			if(!result) {
				throw new McpException("Failed to load ROM: " + filepath);
			}

			RomInfo info = EmuApi.GetRomInfo();
			return "Loaded: " + info.GetRomName() + " (" + info.Format + ", " + info.ConsoleType + ")";
		}

		[McpServerTool(Name = "mesen_get_rom_info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
		 Description("Get information about the currently loaded ROM.")]
		public static string GetRomInfo()
		{
			McpToolHelper.EnsureRunning();

			RomInfo info = EmuApi.GetRomInfo();
			return info.GetRomName() + " " + info.Format + " " + info.ConsoleType + " " + EmuApi.GetRomHash(HashType.Sha1);
		}

		[McpServerTool(Name = "mesen_playback", ReadOnly = false, Destructive = false, OpenWorld = false),
		 Description("Control emulation playback. Use action 'pause' to pause or 'resume' to resume emulation.")]
		public static string Playback(
			[Description("Action to perform: 'pause' or 'resume'")] string action)
		{
			McpToolHelper.EnsureRunning();

			switch(action.ToLowerInvariant()) {
				case "pause":
					EmuApi.Pause();
					return "Paused.";
				case "resume":
					EmuApi.Resume();
					return "Resumed.";
				default:
					throw new McpException("Invalid action: " + action + ". Use 'pause' or 'resume'.");
			}
		}

		[McpServerTool(Name = "mesen_get_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
		 Description("Get the current emulator status: running or paused (with the reason when known, e.g. which breakpoint was hit and by what access), and timing info.")]
		public static string GetStatus()
		{
			if(!EmuApi.IsRunning()) {
				return "Not running.";
			}

			RomInfo info = EmuApi.GetRomInfo();
			CpuType mainCpu = info.ConsoleType.GetMainCpuType();
			TimingInfo timing = EmuApi.GetTimingInfo(mainCpu);

			string state = EmuApi.IsPaused() ? "Paused" + DescribePauseReason() : "Running";
			return state + " " + info.ConsoleType + " " + info.GetRomName() + " " + Math.Round(timing.Fps, 2) + "fps frame=" + timing.FrameCount;
		}

		/// <summary>
		/// Why emulation is paused, e.g. " (breakpoint #0 $00:EC0A SnesMemory, hit by ExecOpCode at $00:EC0A) PC=$00:EC0A",
		/// or "" when not known (e.g. paused from the UI without the debugger).
		/// </summary>
		private static string DescribePauseReason()
		{
			BreakEvent? lastBreak = McpBreakTracker.LastBreak;
			if(lastBreak == null) {
				return "";
			}
			BreakEvent evt = lastBreak.Value;

			StringBuilder sb = new(" (");
			if(evt.Source == BreakSource.Breakpoint) {
				// Breakpoint ids are indexes into BreakpointManager's list, the same numbering mesen_breakpoint 'list' shows
				sb.Append("breakpoint #").Append(evt.BreakpointId);
				Breakpoint? bp = Dispatcher.UIThread.Invoke(() => BreakpointManager.GetBreakpointById(evt.BreakpointId));
				if(bp != null) {
					sb.Append(' ').Append(McpToolHelper.FormatAddress(bp.StartAddress, bp.MemoryType));
					if(bp.EndAddress != bp.StartAddress) {
						sb.Append('-').Append(McpToolHelper.FormatAddress(bp.EndAddress, bp.MemoryType));
					}
					sb.Append(' ').Append(bp.MemoryType);
				}

				// The access that triggered it; DmaRead/DmaWrite means a DMA transfer, not the CPU at PC
				MemoryOperationInfo op = evt.Operation;
				sb.Append(", hit by ").Append(op.Type).Append(" at ").Append(McpToolHelper.FormatAddress(op.Address, op.MemType));
				if(op.Type != MemoryOperationType.ExecOpCode && op.Type != MemoryOperationType.ExecOperand) {
					sb.Append(" value=$").Append(op.Value.ToString("X2"));
				}
			} else {
				sb.Append(evt.Source);
			}
			sb.Append(')');

			if(McpToolHelper.IsDebuggerReady()) {
				uint pc = DebugApi.GetProgramCounter(evt.SourceCpu, false);
				sb.Append(" PC=").Append(McpToolHelper.FormatCpuAddress(pc, evt.SourceCpu));
			}
			return sb.ToString();
		}

		[McpServerTool(Name = "mesen_take_screenshot", ReadOnly = true, Destructive = false, OpenWorld = false),
		 Description("Take a screenshot of the current frame. Returns the image as base64 PNG. Optionally saves to file.")]
		public static IEnumerable<ContentBlock> TakeScreenshot(
			[Description("Optional file path to also save the PNG to disk")] string? outputFile = null)
		{
			McpToolHelper.EnsureRunning();

			string tempPath = Path.Combine(Path.GetTempPath(), "mesen_screenshot_" + Guid.NewGuid().ToString("N") + ".png");
			try {
				Int32 bytesWritten = McpNativeApi.TakeScreenshotToFile(tempPath);
				if(bytesWritten <= 0) {
					throw new McpException("Failed to capture screenshot.");
				}

				byte[] pngData = File.ReadAllBytes(tempPath);

				if(!string.IsNullOrEmpty(outputFile)) {
					McpToolHelper.WriteOutputFile(outputFile, path => File.Copy(tempPath, path, true));
					yield return new TextContentBlock { Text = "Saved to " + outputFile };
				}

				yield return ImageContentBlock.FromBytes(pngData, "image/png");
			} finally {
				try { File.Delete(tempPath); } catch { }
			}
		}

		[McpServerTool(Name = "mesen_save_state", ReadOnly = false, Destructive = false, OpenWorld = false),
		 Description("Save or load emulator state. Use action 'save' to save state, or 'load' to load state, from a numbered slot (1-10) or a file path.")]
		public static string SaveState(
			[Description("Action to perform: 'save' or 'load'")] string action,
			[Description("Slot number (1-10) or absolute file path")] string slotOrPath)
		{
			McpToolHelper.EnsureRunning();

			switch(action.ToLowerInvariant()) {
				case "save":
					if(int.TryParse(slotOrPath, out int saveSlot) && saveSlot >= 1 && saveSlot <= 10) {
						EmuApi.SaveState((uint)saveSlot);
						return "Saved slot " + saveSlot;
					} else {
						EmuApi.SaveStateFile(slotOrPath);
						return "Saved to " + slotOrPath;
					}

				case "load":
					if(int.TryParse(slotOrPath, out int loadSlot) && loadSlot >= 1 && loadSlot <= 10) {
						EmuApi.LoadState((uint)loadSlot);
						return "Loaded slot " + loadSlot;
					} else {
						if(!File.Exists(slotOrPath)) {
							throw new McpException("File not found: " + slotOrPath);
						}
						EmuApi.LoadStateFile(slotOrPath);
						return "Loaded from " + slotOrPath;
					}

				default:
					throw new McpException("Invalid action: " + action + ". Use 'save' or 'load'.");
			}
		}
	}
}
