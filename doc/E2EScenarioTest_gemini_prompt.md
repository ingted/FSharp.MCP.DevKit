Read [/workspace/home/mcp/FSharp.MCP.DevKit/doc/E2EScenarioTest_gemini.md](/workspace/home/mcp/FSharp.MCP.DevKit/doc/E2EScenarioTest_gemini.md) and follow it exactly.

Task:
1. Create a fresh remote out-of-process net10 FSI host.
2. Create a fresh session on that host.
3. Read lines 1..76 of `/gemini4/work/coldfar-symbolics/experiments/generate_real_charts.inspect_930k_vs_30k.fsx` using fsharp-devkit tools.
4. Rewrite only the code string as instructed by the document.
5. Execute the rewritten code on the remote session.
6. Wait for completion using fsharp-devkit tools.
7. Evaluate:
`cfar.Cfarta.[int scale].[set [Scale scale; USING 7; MACD [decimal 13; decimal 21; decimal 7]], false, CFTAMode.CFTAMin].c`
8. Return only a short final summary with host id, session id, async status, and the evaluated value.

Rules:
- Use only fsharp-devkit MCP tools when possible.
- Do not use delegation or generalist.
- Do not stop after reading the document.
- Do not use local shell scripts.
- If async status is still Running, keep checking with get_async_status until completion or until the tool itself indicates failure.
