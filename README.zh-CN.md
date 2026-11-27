# csharp-decompiler-skills

[English](README.md)

一组通用 AI Agent 技能（Skills），用于 .NET 程序集元数据与 XML 注释离线索引、符号/接口查询以及 IL 反编译，适合分析闭源 SDK（如 Revit、AutoCAD、Plant 3D 或 NuGet 包）与逆向工程业务逻辑。

## 包含技能

| 技能 | 描述 |
| --- | --- |
| [csharp-definition-lookup](csharp-definition-lookup/SKILL.md) | 基于 `System.Reflection.Metadata` + SQLite FTS5 的离线 .NET API 索引与查询工具。支持全文与驼峰分词搜索、干净的 C# 签名骨架（含继承成员、扩展方法与 XML 注释）、继承树与按类型反查方法。 |
| [csharp-decompiler](csharp-decompiler/SKILL.md) | 使用 `ilspycmd` 将 .NET IL 程序集（`.dll`、`.exe`）反编译为 C# 源码。仅在需要查看方法体、内部副作用或导出完整项目时使用。 |

---

## 快速开始

### 前置条件

- 已安装 [.NET 8+ SDK / Runtime](https://dotnet.microsoft.com/download)
- 已安装 **ilspycmd**（`csharp-decompiler` 所需）：
  ```bash
  dotnet tool install -g ilspycmd
  ```

### 构建 / 发布 `apilookup`（可选，仓库已预编译 Windows 版）

```powershell
# 编译并发布框架依赖单文件二进制到 csharp-definition-lookup/scripts/
.\tools\build.ps1

# 运行单元测试
dotnet test .\src\ApiLookup.Tests\ApiLookup.Tests.csproj -c Release
```

Linux/macOS 可直接通过 `dotnet run --project src/ApiLookup -- <参数>` 运行，或使用 `-r linux-x64` / `-r osx-arm64` 发布。

---

## csharp-definition-lookup（`apilookup`）

基于 `System.Reflection.Metadata` + SQLite FTS5，直接读取 PE 元数据与同名 `.xml` 文档注释并写入本地缓存索引——无需加载进宿主进程，也无需反编译充满噪声的 IL。

### 构建命名 SDK 索引（一次构建、长期秒查）

```powershell
# AutoCAD + Plant 3D（通过 --include 过滤掉无关第三方 UI 库）
.\csharp-definition-lookup\scripts\apilookup.exe index --name acad2025 --no-recursive `
  --dir "C:\Program Files\Autodesk\AutoCAD 2025" `
  --dir "C:\Program Files\Autodesk\AutoCAD 2025\PLNT3D" `
  --include "ac*mgd.dll" --include "Ac*.dll" --include "Autodesk.*.dll" --include "PnP*.dll" --include "PnId*.dll"

# Revit（自动加载同级 RevitAPI.xml / RevitAPIUI.xml 注释）
.\csharp-definition-lookup\scripts\apilookup.exe index --name revit2026 `
  --dll "C:\Program Files\Autodesk\Revit 2026\RevitAPI.dll" `
  --dll "C:\Program Files\Autodesk\Revit 2026\RevitAPIUI.dll"
```

### 常用查询

```powershell
# 全文 + 驼峰分词搜索类型、成员与 XML 摘要
apilookup.exe search "offscreen device" --index acad2025
apilookup.exe search "create ceiling" --index revit2026

# 查看干净的 C# 骨架（支持继承成员 --inherited、扩展方法 --ext、成员过滤 --member、完整文档 --docs）
apilookup.exe show DocumentCollection --index acad2025 --ns Autodesk.AutoCAD --ext --member Open
apilookup.exe show Equipment --index acad2025 --ns Autodesk.ProcessPower.PnP3dObjects --member Port --inherited
apilookup.exe show Wall --index revit2026 --member Create --docs

# 继承树与按类型反查方法（返回/接收该类型的所有方法与属性）
apilookup.exe hierarchy Autodesk.ProcessPower.PnP3dObjects.Part --index acad2025
apilookup.exe by-type DataLinksManager3d --index acad2025

# 直接查询 .csproj 引用（自动按文件指纹缓存）
apilookup.exe search Asset --project C:\repo\MyApp\MyApp.csproj --list-refs
```

---

## csharp-decompiler（`ilspycmd`）

当签名与 XML 注释不足以判断内部行为（例如隐藏副作用、原生调用前置条件、弹窗类名）时，再用 `ilspycmd` 反编译具体类型：

```powershell
# Windows (PowerShell — ilspycmd 输出在 stderr，务必重定向到文件再读)
ilspycmd -t <FullNamespace.ClassName> <assembly_path> 2>&1 > "$env:TEMP\MyType.cs"

# 导出完整项目
ilspycmd -p -o <output_directory> <assembly_path>
```

---

## 推荐工作流

```text
需要查找接口 / 签名 / 用法
          │
          ▼
csharp-definition-lookup (apilookup)  ──►  FTS5 全文检索、C# 骨架、XML 注释、
          │                                继承链、扩展方法、精确 DLL 路径
          ▼（仅当需要查看方法体 / 内部副作用时）
csharp-decompiler (ilspycmd)          ──►  反编译完整 C# 实现源码
```

---

## 目录结构

```text
src/
  ApiLookup/               # .NET 8 CLI（System.Reflection.Metadata + Microsoft.Data.Sqlite FTS5）
  ApiLookup.Tests/         # xUnit 单元测试
tools/
  build.ps1                # 发布 apilookup 到 csharp-definition-lookup/scripts/
csharp-definition-lookup/
  SKILL.md                 # 技能描述与用法（供 AI Agent 加载）
  scripts/
    apilookup.exe          # 预编译 Windows 单文件 CLI（框架依赖）
    TypeScanner.csx        # 旧版 dotnet-script 扫描脚本
csharp-decompiler/
  SKILL.md                 # 技能描述与用法（供 AI Agent 加载）
```

---

## License

[MIT](LICENSE)
