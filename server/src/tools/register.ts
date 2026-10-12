import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

export async function registerTools(server: McpServer) {
  // 获取当前文件的目录路径
  const __filename = fileURLToPath(import.meta.url);
  const __dirname = path.dirname(__filename);

  // 读取tools目录下的所有文件
  const files = fs.readdirSync(__dirname);

  // 过滤出.ts或.js文件，但排除index文件和register文件
  const toolFiles = files.filter(
    (file) =>
      (file.endsWith(".ts") || file.endsWith(".js")) &&
      file !== "index.ts" &&
      file !== "index.js" &&
      file !== "register.ts" &&
      file !== "register.js"
  );

  // Formcraft tool profile: hide upstream tools Formcraft does not use, so their descriptions do not
  // load into every conversation. FMC_TOOL_PROFILE=full restores every tool.
  const profile = (process.env.FMC_TOOL_PROFILE || "fmc").toLowerCase();
  const hiddenInFmcProfile = new Set([
    "say_hello", "color_elements", "tag_all_walls", "tag_all_rooms", "store_project_data", "store_room_data",
    "query_stored_data", "export_room_data", "search_modules", "use_module",
  ]);

  // 动态导入并注册每个工具
  for (const file of toolFiles) {
    if (profile === "fmc" && hiddenInFmcProfile.has(file.replace(/\.(ts|js)$/, ""))) {
      console.error(`FMC profile: skipped ${file}`);
      continue;
    }
    try {
      // 构建导入路径
      const importPath = `./${file.replace(/\.(ts|js)$/, ".js")}`;

      // 动态导入模块
      const module = await import(importPath);

      // 查找并执行注册函数
      const registerFunctionName = Object.keys(module).find(
        (key) => key.startsWith("register") && typeof module[key] === "function"
      );

      if (registerFunctionName) {
        module[registerFunctionName](server);
        console.error(`已注册工具: ${file}`);
      } else {
        console.warn(`警告: 在文件 ${file} 中未找到注册函数`);
      }
    } catch (error) {
      console.error(`注册工具 ${file} 时出错:`, error);
    }
  }
}
