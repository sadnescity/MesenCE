using Mesen.Interop;
using Mesen.Mcp.Models;
using ModelContextProtocol;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Mesen.Mcp.Tools
{
	public static class McpToolHelper
	{
		private static volatile bool _debuggerInitialized;

		public static bool IsDebuggerReady()
		{
			return _debuggerInitialized && EmuApi.IsRunning();
		}

		public static void MarkDebuggerInitialized()
		{
			_debuggerInitialized = true;
		}

		public static void EnableDefaultTracing()
		{
			if(!EmuApi.IsRunning()) return;

			RomInfo info = EmuApi.GetRomInfo();
			CpuType mainCpu = info.ConsoleType.GetMainCpuType();
			EnableTracingForCpu(mainCpu);
		}

		public static void EnableTracingForCpu(CpuType cpu)
		{
			string format = GetDefaultTraceFormat(cpu);

			InteropTraceLoggerOptions options = new() {
				Enabled = true,
				UseLabels = true,
				IndentCode = false,
				Format = Encoding.UTF8.GetBytes(format),
				Condition = Encoding.UTF8.GetBytes("")
			};
			Array.Resize(ref options.Format, 1000);
			Array.Resize(ref options.Condition, 1000);

			DebugApi.SetTraceOptions(cpu, options);
		}

		public static string GetDefaultTraceFormat(CpuType cpuType)
		{
			return cpuType switch {
				CpuType.Snes or CpuType.Sa1 =>
					"[Disassembly][Align,24] A:[A,4h] X:[X,4h] Y:[Y,4h] S:[SP,4h] D:[D,4h] DB:[DB,2h] P:[P,8]",
				CpuType.Nes =>
					"[Disassembly][Align,24] A:[A,2h] X:[X,2h] Y:[Y,2h] S:[SP,2h] P:[P,8]",
				CpuType.Gameboy =>
					"[Disassembly][Align,24] A:[A,2h] B:[B,2h] C:[C,2h] D:[D,2h] E:[E,2h] F:[F,2h] H:[H,2h] L:[L,2h] SP:[SP,4h]",
				CpuType.Gba =>
					"[Disassembly][Align,42] ",
				_ =>
					"[Disassembly][Align,24] "
			};
		}

		public static string Serialize<T>(T value)
		{
			JsonTypeInfo<T>? typeInfo = McpJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>;
			if(typeInfo == null) {
				throw new InvalidOperationException("Type " + typeof(T).Name + " is not registered in McpJsonContext");
			}
			return JsonSerializer.Serialize(value, typeInfo);
		}

		public static void EnsureDebuggerReady()
		{
			if(!EmuApi.IsRunning()) {
				throw new McpException("No ROM loaded.");
			}
			if(!_debuggerInitialized) {
				DebugApi.InitializeDebugger();
				_debuggerInitialized = true;
				EnableDefaultTracing();
			}
		}

		public static void EnsureRunning()
		{
			if(!EmuApi.IsRunning()) {
				throw new McpException("No ROM loaded.");
			}
		}

		public static uint ParseAddress(string address)
		{
			if(!TryParseAddress(address, out uint result)) {
				throw new McpException("Invalid address: " + address + ". Use decimal, $hex or 0xhex, or $BB:AAAA (bank:address).");
			}
			return result;
		}

		/// <summary>
		/// Formats an address in the given memory type. CPU addresses on the SNES-family CPUs are 24-bit, so they
		/// are printed the way SNES documentation writes them, $BB:AAAA, with a fixed width: that way they cannot
		/// be mistaken for ROM offsets, which stay plain hex. Other memory types are printed as before.
		/// </summary>
		public static string FormatAddress(uint address, MemoryType memType)
		{
			return memType switch {
				MemoryType.SnesMemory or MemoryType.Sa1Memory or MemoryType.GsuMemory or MemoryType.Cx4Memory =>
					"$" + (address >> 16).ToString("X2") + ":" + (address & 0xFFFF).ToString("X4"),
				_ => "$" + address.ToString("X4")
			};
		}

		/// <summary>Formats an address in a CPU's address space (see <see cref="FormatAddress"/>).</summary>
		public static string FormatCpuAddress(uint address, CpuType cpu)
		{
			return FormatAddress(address, cpu.ToMemoryType());
		}

		/// <summary>
		/// Writes a tool's outputFile, creating missing folders. Failures are reported as McpExceptions so the
		/// caller sees the actual reason: the MCP SDK replaces any other exception with a generic error message.
		/// </summary>
		public static void WriteOutputFile(string path, Action<string> write)
		{
			try {
				string fullPath = Path.GetFullPath(path);
				string? folder = Path.GetDirectoryName(fullPath);
				if(!string.IsNullOrEmpty(folder)) {
					Directory.CreateDirectory(folder);
				}
				write(fullPath);
			} catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
				throw new McpException("Could not write '" + path + "': " + ex.Message);
			}
		}

		public static MemoryType ParseMemoryType(string memoryType)
		{
			if(!Enum.TryParse<MemoryType>(memoryType, true, out MemoryType memType)) {
				throw new McpException("Invalid memory type: " + memoryType + ". Call mesen_list_memory_types to see valid values.");
			}
			return memType;
		}

		public static CpuType ParseCpuType(string cpuType)
		{
			if(!Enum.TryParse<CpuType>(cpuType, true, out CpuType cpu)) {
				throw new McpException("Invalid CPU type: " + cpuType + ". Call mesen_list_cpu_types to see valid values.");
			}
			return cpu;
		}

		public static bool TryParseAddress(string address, out uint result)
		{
			const System.Globalization.NumberStyles hex = System.Globalization.NumberStyles.HexNumber;

			string digits = address.Trim();
			bool isHex = false;
			if(digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
				digits = digits.Substring(2);
				isHex = true;
			} else if(digits.StartsWith("$")) {
				digits = digits.Substring(1);
				isHex = true;
			}

			int colon = digits.IndexOf(':');
			if(colon >= 0) {
				//Bank:address ($BB:AAAA), the usual way to write SNES CPU addresses; always hex
				result = 0;
				string bankDigits = digits.Substring(0, colon);
				string offsetDigits = digits.Substring(colon + 1);
				if(!uint.TryParse(bankDigits, hex, null, out uint bank) || bank > 0xFF) {
					return false;
				}
				if(!uint.TryParse(offsetDigits, hex, null, out uint offset) || offset > 0xFFFF) {
					return false;
				}
				result = (bank << 16) | offset;
				return true;
			}

			return isHex ? uint.TryParse(digits, hex, null, out result) : uint.TryParse(digits, out result);
		}

		public static string FormatFlags6502(byte ps)
		{
			return string.Concat(
				(ps & 0x80) != 0 ? "N" : "n",
				(ps & 0x40) != 0 ? "V" : "v",
				"-",
				(ps & 0x10) != 0 ? "B" : "b",
				(ps & 0x08) != 0 ? "D" : "d",
				(ps & 0x04) != 0 ? "I" : "i",
				(ps & 0x02) != 0 ? "Z" : "z",
				(ps & 0x01) != 0 ? "C" : "c"
			);
		}

		public static bool TryParseValue(string value, out byte result)
		{
			value = value.Trim();
			if(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
				return byte.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out result);
			} else if(value.StartsWith("$")) {
				return byte.TryParse(value.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out result);
			} else {
				return byte.TryParse(value, out result);
			}
		}
	}
}
