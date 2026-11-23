#include "Common.h"
#include "Core/Shared/Emulator.h"
#include "Core/Shared/DebuggerRequest.h"
#include "Core/Shared/Video/VideoDecoder.h"
#include "Core/Debugger/Debugger.h"
#include "Core/Debugger/DebugTypes.h"
#include "Core/Debugger/DebugUtilities.h"
#include "Core/Debugger/Disassembler.h"
#include "Core/Debugger/DisassemblyInfo.h"
#include "Core/Debugger/CdlManager.h"
#include "Core/Debugger/CodeDataLogger.h"
#include "Core/Debugger/LabelManager.h"
#include "Core/Debugger/MemoryDumper.h"

//Native entry points used only by the MCP server (UI/Mcp/McpNativeApi.cs).
//They live in a file of their own, rather than in the upstream *ApiWrapper.cpp files,
//so that rebasing the MCP patch set onto upstream never conflicts on them.

extern unique_ptr<Emulator> _emu;

static void CopyLineText(char dst[1000], const string& src)
{
	size_t len = std::min<size_t>(src.size(), 999);
	memcpy(dst, src.c_str(), len);
	dst[len] = 0;
}

extern "C"
{
	DllExport int32_t __stdcall TakeScreenshotToFile(const char* filepath)
	{
		std::stringstream stream;
		_emu->GetVideoDecoder()->TakeScreenshot(stream);
		if(stream.tellp() > 0) {
			std::ofstream file(filepath, std::ios::binary);
			if(file) {
				file << stream.rdbuf();
				return (int32_t)stream.tellp();
			}
		}
		return 0;
	}

	//Decodes instructionCount instructions starting exactly at a CPU address, whether or not
	//that address is known to be code, without changing anything: code that already ran (or
	//that the CDL marks as code) uses the debugger's cached decode, anything else is decoded
	//on the fly and discarded. The CDL, the disassembly cache and the debugger's display
	//settings are left as they were.
	//An address with a label gets an extra row (Flags = Label, Text = label) before its
	//instruction, so output needs room for up to 2 rows per instruction.
	//Instruction rows are flagged VerifiedCode when the CDL marks them as code, and
	//UnexecutedCode when the CDL does not. Memory without a CDL (e.g. RAM) gets neither,
	//since there is no way to tell.
	DllExport uint32_t __stdcall PeekDisassembly(CpuType cpuType, uint32_t address, uint32_t instructionCount, CodeLineData output[], uint32_t maxRows)
	{
		DebuggerRequest dbgRequest = _emu->GetDebugger(true);
		Debugger* dbg = dbgRequest.GetDebugger();
		if(!dbg || !dbg->HasCpuType(cpuType)) {
			return 0;
		}

		Disassembler* disassembler = dbg->GetDisassembler();
		LabelManager* labelManager = dbg->GetLabelManager();
		CdlManager* cdlManager = dbg->GetCdlManager();
		EmuSettings* settings = _emu->GetSettings();

		MemoryType cpuMemType = DebugUtilities::GetCpuMemoryType(cpuType);
		uint32_t memSize = dbg->GetMemoryDumper()->GetMemorySize(cpuMemType);

		uint32_t rowCount = 0;
		uint32_t addr = address;
		for(uint32_t i = 0; i < instructionCount && addr < memSize; i++) {
			AddressInfo relAddr = { (int32_t)addr, cpuMemType };
			AddressInfo absAddr = dbg->GetAbsoluteAddress(relAddr);
			if(absAddr.Address < 0 || absAddr.Type == MemoryType::SnesRegister) {
				//Unmapped memory or I/O registers: nothing meaningful to decode (same rule as the debugger)
				break;
			}

			string label = labelManager->GetLabel(absAddr);
			if(!label.empty()) {
				if(rowCount >= maxRows) {
					break;
				}
				CodeLineData& labelRow = output[rowCount++];
				labelRow = {};
				labelRow.Address = (int32_t)addr;
				labelRow.AbsoluteAddress = absAddr;
				labelRow.LineCpuType = cpuType;
				labelRow.Flags = LineFlags::Label;
				CopyLineText(labelRow.Text, label);
			}

			if(rowCount >= maxRows) {
				break;
			}

			CodeDataLogger* cdl = cdlManager->GetCodeDataLogger(absAddr.Type);
			DisassemblyInfo info = disassembler->GetDisassemblyInfo(absAddr, addr, dbg->GetCpuFlags(cpuType, addr), cpuType);

			CodeLineData& row = output[rowCount++];
			row = {};
			row.Address = (int32_t)addr;
			row.AbsoluteAddress = absAddr;
			row.LineCpuType = cpuType;
			row.OpSize = info.GetOpSize();
			if(cdl) {
				row.Flags = cdl->IsCode(absAddr.Address) ? LineFlags::VerifiedCode : LineFlags::UnexecutedCode;
			}

			string text;
			info.GetDisassembly(text, addr, labelManager, settings);
			CopyLineText(row.Text, text);
			info.GetByteCode(row.ByteCode);
			CopyLineText(row.Comment, labelManager->GetComment(absAddr));

			addr += std::max<uint32_t>(row.OpSize, 1);
		}
		return rowCount;
	}
}
