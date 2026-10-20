# 构建踩坑：仓库根 + WPF wpftmp 临时工程 CS0579 特性重复

## 现象

添加 `SymbolDesigner.Cli/` 子工程（同仓库、主工程在根目录）后，构建主工程 `SymbolDesigner.csproj` 报大量 CS0579：

```
obj\Debug\net8.0-windows\.NETCoreApp,Version=v8.0.AssemblyAttributes.cs(4,12):
error CS0579: "global::System.Runtime.Versioning.TargetFrameworkAttribute"特性重复 [SymbolDesigner_xxx_wpftmp.csproj]
...（此外 8 个 Assembly*Attribute 特性重复）
```

反复 `dotnet clean`、清空 `obj/bin`、固定 SDK 版本均无法解决——因为它不是缓存残留，而是每次构建都会重新生成、重新冲突。

## 根因

WPF 的 MarkupCompilePass 会在**工程所在目录**生成一个随机的临时工程 `<Name>_<random>_wpftmp.csproj` 并构建它，用于在编 XAML 时解析"本工程内定义的类型"。

本仓库根同时容纳两个工程（主工程在根，CLI 在子目录）：

- 主工程 `SymbolDesigner.csproj`（根）的 wpftmp 临时工程也生成在根；
- wpftmp 工程沿用 `Microsoft.NET.Sdk` 的**默认 `**/*.cs` Compile glob**；
- 于是它把子目录 `SymbolDesigner.Cli\` 下的 **源码 + `obj` 生成的 `.AssemblyInfo.cs` / `.NETCoreApp...AssemblyAttributes.cs`** 一并收编进编译集；
- 这些生成文件里的 `TargetFrameworkAttribute` / `AssemblyTitleAttribute` 等 Assembly 特性，与【主工程 + wpftmp 自己】生成的同名特性文件**重复** → CS0579。

用 `dotnet msbuild <wpftmp.csproj> -getItem:Compile` 验证：103 个 Compile 项，明确包含 `SymbolDesigner.Cli\*` 及 `SymbolDesigner.Cli\obj\Debug\...\AssemblyAttributes.cs` 等。

> 顺带说明：主工程自身 `dotnet msbuild SymbolDesigner.csproj -getItem:Compile` 同样收编了 CLI 源码——即 CLI 加入后主工程其实一直编不过，只是错误只在 XAML 标记编译阶段（wpftmp）暴露才最明显。

## 修复

在**仓库根**新建 `Directory.Build.props`，把子工程目录从默认项排除（对主工程与 wpftmp 都生效，因为它们的工程目录都是仓库根，相对路径一致）：

```xml
<Project>
  <PropertyGroup>
    <DefaultItemExcludes>$(DefaultItemExcludes);SymbolDesigner.Cli\**</DefaultItemExcludes>
  </PropertyGroup>
</Project>
```

- 对主工程 / wpftmp：排除路径相对仓库根 = `SymbolDesigner.Cli\**` → 正确剥离误收编项；
- 对 CLI 工程自身：其目录已是 `SymbolDesigner.Cli\`，相对路径 `SymbolDesigner.Cli\**` 匹配不到任何文件 → **无副作用**，不误伤它自己的源码。

验证：主工程 → `SymbolDesigner.dll`、CLI → `sym.dll`，均 `0 个错误`。

## 经验

- 仓库根放多个工程且主工程在根时，务必用 `Directory.Build.props` 的 `DefaultItemExcludes` 隔离相邻工程的 `obj/**` 与子目录源码，否则 WPF wpftmp 临时工程会跨界收编。
- WPF 报 `error ... wpftmp.csproj` 时，别只盯着 `obj` 清理，先思考临时工程的默认 glob 收编了什么外面的文件；用 `-getItem:Compile` 直接核对最有效。
- 排查技巧：`dotnet build -p:GenerateTemporaryTargetAssemblyDebuggingInformation=true` 可让临时工程文件保留不删，便于直接读取其收编范围。