# csharp-decompiler-skills

[English](README.md)

一组通用 AI Agent 技能（Skills），用于 C# 程序集反编译与符号定位，帮助你分析 .NET 程序集、逆向工程业务逻辑，以及快速定位类型所在的命名空间和程序集。

## 包含技能

| 技能 | 描述 |
|------|------|
| [csharp-decompiler](csharp-decompiler/SKILL.md) | 将 .NET IL 程序集（`.dll`、`.exe`）反编译为 C# 源码，支持列出类型、反编译指定类型或导出完整项目 |
| [csharp-definition-lookup](csharp-definition-lookup/SKILL.md) | 从项目引用中快速查找 C# 类的命名空间与所属程序集，支持 NuGet 包解析 |

---

## 快速开始

### 前置条件

- [.NET SDK](https://dotnet.microsoft.com/download) 已安装
- **ilspycmd** 已安装：
  ```bash
  dotnet tool install -g ilspycmd
  ```
- **dotnet-script** 已安装（`csharp-definition-lookup` 所需）：
  ```bash
  dotnet tool install -g dotnet-script
  ```
- Python 3.x

### 安装验证

```bash
dotnet --version
ilspycmd --version
```

---

## csharp-decompiler

直接使用 `ilspycmd` 将 .NET 程序集反编译为 C# 源码。

### 列出程序集内的所有类型

```bash
ilspycmd -l s <assembly_path>
```

### 反编译指定类型

**Windows (PowerShell)：**
```powershell
ilspycmd -t <FullNamespace.ClassName> <assembly_path> 2>&1 > C:\temp\MyType.cs
```

**Linux/macOS：**
```bash
ilspycmd -t <FullNamespace.ClassName> <assembly_path> > /tmp/MyType.cs 2>&1
```

> 嵌套类使用 `+` 分隔符：`Namespace.OuterClass+InnerClass`

> **注意**：`ilspycmd` 将输出写入 stderr，请务必使用文件重定向（`2>&1 >`）并通过读取输出文件获取内容——不要依赖终端直接输出。

### 导出完整项目

```bash
ilspycmd -p -o <output_directory> <assembly_path>
```

---

## csharp-definition-lookup

在项目引用（`.csproj` / NuGet）中定位某个类型的命名空间和 DLL 路径。

### 基本用法

```bash
python csharp-definition-lookup/scripts/csharp_lookup.py <SymbolName> --project <PathToCsproj>
```

### 列出所有已解析的引用

```bash
python csharp-definition-lookup/scripts/csharp_lookup.py <SymbolName> --project <PathToCsproj> --list-refs
```

### 获取简洁 API 摘要（推荐用于大型类型）

```bash
python csharp-definition-lookup/scripts/csharp_lookup.py <SymbolName> --project <PathToCsproj> --summary
```

### 其他选项

| 选项 | 说明 |
|------|------|
| `--summary` | 仅显示成员签名（不含方法体），适合初次了解大型或陌生类型。 |
| `--full` | 输出完整反编译内容，取消默认的 60 行截断限制。 |
| `--namespace <prefix>` | 按命名空间前缀过滤结果（例如 `Autodesk`）。 |
| `--dll <path>` | 直接搜索指定 DLL，不从 `.csproj` 解析。 |

> 使用前请先执行 `dotnet restore`，以生成 `obj/project.assets.json`。

---

## 推荐工作流

```
不确定类型位置
      │
      ▼
csharp-definition-lookup   ──►  获得：完整命名空间 + DLL 路径
      │
      ▼
csharp-decompiler           ──►  获得：完整 C# 源码 / 项目结构
```

---

## 目录结构

```
csharp-decompiler/
  SKILL.md               # 技能描述与用法（供 AI Agent 加载）

csharp-definition-lookup/
  SKILL.md               # 技能描述与用法（供 AI Agent 加载）
  scripts/
    csharp_lookup.py     # 符号查找脚本
    TypeScanner.csx      # C# Script 辅助扫描器
```

---

## License

[MIT](LICENSE)
