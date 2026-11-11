#!/usr/bin/env node
// PostToolUse（Edit|Write|MultiEdit）：改 poc 產品程式碼時，若新內容疑似把業務政策寫死，提醒 agent 判斷。
// 只提醒、不擋：一律 exit 0；讀不到輸入或解析失敗就靜默結束（fail-open）。
let raw = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (c) => (raw += c));
process.stdin.on("end", () => {
  let input;
  try { input = JSON.parse(raw); } catch { return; }
  const ti = input.tool_input ?? {};
  const file = String(ti.file_path ?? "").replace(/\\/g, "/");
  if (!/(^|\/)poc\/(backend\/src|frontend\/src)\//.test(file)) return;
  if (/(\.spec\.|\.test\.|\.vitest\.|Tests?\.cs$|\/tests?\/|\/__tests__\/|\/i18n\/)/.test(file)) return;

  const text = [ti.content, ti.new_string, ...(ti.edits ?? []).map((e) => e?.new_string)]
    .filter((s) => typeof s === "string")
    .join("\n");
  if (!text) return;

  // 代碼樣式（小寫-連字號，如 xxx-yyy）是通用判斷，不列舉特定應用程式。
  const code = `["'][a-z][a-z0-9]*-[a-z0-9-]+["']`;
  const patterns = [
    [/(module|app)Key\s*(===?|!==?)\s*["']|["'][\w-]+["']\s*(===?|!==?)\s*\w*(module|app)Key|(module|app)Key\.Equals\(\s*["']/i, "用模組／應用程式 key 字串分支"],
    [new RegExp(`${code}\\s*(===?|!==?)|(===?|!==?)\\s*${code}|\\bcase\\s+${code}|\\.Equals\\(\\s*${code}`), "比對特定模組／應用程式代碼"],
    [/\bconst\s+\w+\s+(Max|Min|Default)\w*/, "C# 常數上限／預設值"],
    [/\breadonly\s+[\w<>,?\s]+?\s+(Max|Min|Default)\w*\s*=/, "C# 唯讀上限／預設值"],
    [/\b(const|let)\s+(MAX|MIN|DEFAULT)_[A-Z_]+\s*=/, "前端常數上限／預設值"],
    [/(TimeSpan\.From|\.Add)(Days|Hours|Minutes)\(\s*-?\d+/, "寫死的天數／時數"],
  ];
  const hits = patterns.filter(([re]) => re.test(text)).map(([, label]) => label);
  if (hits.length === 0) return;

  const msg =
    `寫死規則提醒（${file.slice(file.lastIndexOf("poc/"))}）：${hits.join("、")}。` +
    "依 AGENTS.md 鐵則：業務政策（上限、預設值、天數、模組特例）要可設定、單一來源；" +
    "若是完整性守衛、安全檢查或技術限制可以留下，但要列進設計文件的「可寫死清單」。";
  process.stdout.write(
    JSON.stringify({ hookSpecificOutput: { hookEventName: "PostToolUse", additionalContext: msg } })
  );
});
