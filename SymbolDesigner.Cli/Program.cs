using System.Text;
using SymbolDesigner.Models;

namespace SymbolDesigner.Cli;

/// <summary>sym — SymbolDesigner 无头 CLI：用结构化 JSON 生成/追加/检查 .sym.json 工程（供外部 AI-agent 画图）。
/// 退出码：0 成功 | 1 参数错 | 2 校验失败 | 3 IO 错 | 10 未捕获异常。</summary>
internal static class Program
{
    private const string DefaultOut = "project.sym.json";

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 0)
            {
                PrintHelp();
                return 1;
            }
            string cmd = args[0];
            bool json = args.Skip(1).Contains("--json");
            var rest = args.Skip(1).Where(a => a != "--json").ToArray();
            var r = Args.Parse(rest);

            return cmd switch
            {
                "new" => CmdNew(r, json),
                "add" => CmdAdd(r, json),
                "info" => CmdInfo(r, json),
                "validate" => CmdValidate(r, json),
                "render" => CmdRender(r, json),
                "edit" => CmdEdit(r, json),
                "remove" => CmdRemove(r, json),
                "help" or "--help" or "-h" => Help(json),
                _ => throw new ArgError($"未知子命令: {cmd}"),
            };
        }
        catch (CliException e)
        {
            Console.Error.WriteLine(e.Message);
            return e.Code;
        }
        catch (ArgError e)
        {
            Console.Error.WriteLine("参数错误: " + e.Message + "  (用 sym help 查看用法)");
            return 1;
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine("输入格式无效: " + e.Message);
            return 2;
        }
        catch (IOException e)
        {
            Console.Error.WriteLine("IO 错误: " + e.Message);
            return 3;
        }
        catch (UnauthorizedAccessException e)
        {
            Console.Error.WriteLine("IO 错误(无权限): " + e.Message);
            return 3;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("未捕获异常: " + e.Message);
            return 10;
        }
    }

    private static int CmdNew(ParsedArgs r, bool json)
    {
        string outPath = r.Get("output") ?? DefaultOut;
        var proj = new SymbolProject { CanvasWidth = 1920, CanvasHeight = 1080 };
        if (r.Has("dsl"))
            MergeDslInto(proj, ReadDslChecked(r));
        ApplyCanvasOverride(proj, r.Get("canvas"));
        WriteProject(outPath, proj);
        if (json)
            JsonReport.Print(new { ok = true, path = outPath, canvasWidth = proj.CanvasWidth, canvasHeight = proj.CanvasHeight, primitiveCount = proj.Primitives.Count });
        else
            JsonReport.Log($"已生成工程 {Path.GetFullPath(outPath)}，图元 {proj.Primitives.Count} 个");
        return 0;
    }

    private static int CmdAdd(ParsedArgs r, bool json)
    {
        var target = r.Positionals.Count == 1 ? r.Positionals[0] : null;
        if (target is null) throw new ArgError("add 需要一个参数: <project.sym.json>");
        string dslSource = r.Get("dsl") ?? throw new ArgError("缺少必填选项 --dsl <file|->");

        var proj = LoadProject(target);
        var added = DslToProject.BuildPrimitives(ReadDslChecked(r).Primitives, proj.Primitives.Count);
        proj.Primitives.AddRange(added);

        string outPath = r.Get("output") ?? target;
        WriteProject(outPath, proj);
        if (json)
            JsonReport.Print(new { ok = true, path = outPath, existing = proj.Primitives.Count - added.Count, added = added.Count, total = proj.Primitives.Count });
        else
            JsonReport.Log($"已追加 {added.Count} 个图元到 {Path.GetFullPath(outPath)}（共 {proj.Primitives.Count}）");
        return 0;
    }

    private static int CmdInfo(ParsedArgs r, bool json)
    {
        var target = r.Positionals.Count == 1 ? r.Positionals[0] : null;
        if (target is null) throw new ArgError("info 需要一个参数: <project.sym.json>");
        var proj = LoadProject(target);

        var list = proj.Primitives.Select(d => new { id = d.Id, type = d.TypeKey, x = d.X, y = d.Y, width = d.Width, height = d.Height, zIndex = d.ZIndex }).ToList();
        int bools = proj.Primitives.Count(d => d.TypeKey == "BooleanPath");
        if (json)
            JsonReport.Print(new
            {
                ok = true,
                canvasWidth = proj.CanvasWidth,
                canvasHeight = proj.CanvasHeight,
                primitiveCount = proj.Primitives.Count,
                primitives = list,
                arrayInstanceCount = proj.ArrayInstances?.Count ?? 0,
                constraintCount = proj.Constraints?.Count ?? 0,
                note = bools > 0 ? "工程含 BooleanPath（CLI 不支持创建，但原样保留）" : (string?)null,
            });
        else
        {
            JsonReport.Log($"工程 {target}: 画布 {proj.CanvasWidth}x{proj.CanvasHeight}，图元 {proj.Primitives.Count} 个");
            foreach (var x in list)
                JsonReport.Log($"  #{x.zIndex} {x.type,-15} x={x.x:0.##} y={x.y:0.##} w={x.width:0.##} h={x.height:0.##} id={x.id}");
        }
        return 0;
    }

    private static int CmdValidate(ParsedArgs r, bool json)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        bool any = false;

        if (r.Has("dsl"))
        {
            any = true;
            var dsl = Dsl.Parse(Dsl.ReadSource(r.Get("dsl")!));
            var v = Validate.Check(dsl);
            errors.AddRange(v.Errors); warnings.AddRange(v.Warnings);
        }
        if (r.Positionals.Count == 1)
        {
            any = true;
            try { LoadProject(r.Positionals[0]); warnings.Add("工程可正常解析"); }
            catch (Exception e) { errors.Add("工程无法解析: " + e.Message); }
        }
        if (!any) throw new ArgError("validate 需要 --dsl <file> 或 <project.sym.json>，或两者都提供");

        if (json)
            JsonReport.Print(new { ok = errors.Count == 0, errors, warnings });
        else
        {
            foreach (var w in warnings) JsonReport.Log("[warn] " + w);
            foreach (var e in errors) JsonReport.Log("[error] " + e);
            if (errors.Count == 0) JsonReport.Log("校验通过");
        }
        return errors.Count == 0 ? 0 : 2;
    }

    private static int CmdRender(ParsedArgs r, bool json)
    {
        var target = r.Positionals.Count == 1 ? r.Positionals[0] : null;
        if (target is null) throw new ArgError("render 需要一个参数: <project.sym.json>");
        var proj = LoadProject(target);

        string outPath = r.Get("output") ?? Path.ChangeExtension(target, ".png");
        var size = ParseSize(r.Get("size"), proj.CanvasWidth, proj.CanvasHeight);
        Renderer.Render(proj, outPath, size.w, size.h);

        if (json)
            JsonReport.Print(new { ok = true, path = Path.GetFullPath(outPath), width = size.w, height = size.h });
        else
            JsonReport.Log($"已渲染预览 {Path.GetFullPath(outPath)}（{size.w}x{size.h}px）");
        return 0;
    }

    /// <summary>解析 --size WxH；缺省按画布纵横比自动取最大边 1280 的等比尺寸。</summary>
    private static (int w, int h) ParseSize(string? size, double cw, double ch)
    {
        int w, h;
        if (size is not null)
        {
            var sp = size.Split('x', 'X');
            if (sp.Length != 2 || !int.TryParse(sp[0], out w) || !int.TryParse(sp[1], out h) || w <= 0 || h <= 0)
                throw new ArgError($"--size 需要 WxH（int），如 1920x1080，得到 {size}");
            return (w, h);
        }
        double scale = 1280.0 / Math.Max(1, cw);
        w = (int)Math.Round(Math.Max(1, cw * scale));
        h = (int)Math.Round(Math.Max(1, ch * scale));
        return (w, h);
    }

    /// <summary>按 id 定位图元；找不到 → IOException(3)。</summary>
    private static PrimitiveItemDto FindPrimitive(SymbolProject proj, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgError("缺少必填选项 --id <图元id>（用 sym info 查看）");
        var dto = proj.Primitives.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (dto is null) throw new IOException($"未找到 id={id} 的图元（用 sym info <proj> 查看现有 id）");
        return dto;
    }

    private static int CmdEdit(ParsedArgs r, bool json)
    {
        var target = r.Positionals.Count == 1 ? r.Positionals[0] : null;
        if (target is null) throw new ArgError("edit 需要一个参数: <project.sym.json>");
        string id = r.Get("id") ?? throw new ArgError("edit 需要 --id <图元id>");
        if (r.Sets.Count == 0) throw new ArgError("edit 至少需要一条 --set field=value");

        var proj = LoadProject(target);
        var dto = FindPrimitive(proj, id);
        foreach (var set in r.Sets)
        {
            int eq = set.IndexOf('=');
            if (eq <= 0) throw new ArgError($"--set 需要 field=value 格式，得到: {set}");
            EditFields.Apply(dto, set[..eq].Trim(), set[(eq + 1)..].Trim());
        }

        string outPath = r.Get("output") ?? target;
        WriteProject(outPath, proj);
        if (json)
            JsonReport.Print(new { ok = true, path = outPath, id, changed = r.Sets.Count });
        else
            JsonReport.Log($"已编辑图元 {id}（{r.Sets.Count} 个字段）→ {Path.GetFullPath(outPath)}");
        return 0;
    }

    private static int CmdRemove(ParsedArgs r, bool json)
    {
        var target = r.Positionals.Count == 1 ? r.Positionals[0] : null;
        if (target is null) throw new ArgError("remove 需要一个参数: <project.sym.json>");
        string id = r.Get("id") ?? throw new ArgError("remove 需要 --id <图元id>");

        var proj = LoadProject(target);
        var dto = FindPrimitive(proj, id);
        proj.Primitives.Remove(dto);

        string outPath = r.Get("output") ?? target;
        WriteProject(outPath, proj);
        if (json)
            JsonReport.Print(new { ok = true, path = outPath, id, removed = true, remaining = proj.Primitives.Count });
        else
            JsonReport.Log($"已删除图元 {id} → {Path.GetFullPath(outPath)}（剩余 {proj.Primitives.Count} 个）");
        return 0;
    }

    private static int Help(bool json)
    {
        if (json) JsonReport.Print(new { ok = true });
        PrintHelp();
        return 0;
    }

    // ===== helpers =====

    /// <summary>读 DSL 源并校验；校验失败 → 退出码 2 中止（错误详细列出到 stderr）。</summary>
    private static DslProject ReadDslChecked(ParsedArgs r)
    {
        string source = r.Get("dsl")!;
        var dsl = Dsl.Parse(Dsl.ReadSource(source));
        var v = Validate.Check(dsl);
        if (!v.IsValid)
            throw ValidationFail("new/add", v);
        return dsl;
    }

    private static void MergeDslInto(SymbolProject proj, DslProject dsl)
    {
        if (dsl.CanvasWidth is double w) proj.CanvasWidth = w;
        if (dsl.CanvasHeight is double h) proj.CanvasHeight = h;
        if (dsl.CanvasBackgroundColor is string c) proj.CanvasBackgroundColor = c;
        proj.Primitives.AddRange(DslToProject.BuildPrimitives(dsl.Primitives, proj.Primitives.Count));
    }

    private static void ApplyCanvasOverride(SymbolProject proj, string? canvas)
    {
        if (canvas is null) return;
        var sp = canvas.Split('x', 'X');
        if (sp.Length != 2 || !double.TryParse(sp[0], out double w) || !double.TryParse(sp[1], out double h) || w <= 0 || h <= 0)
            throw new ArgError($"--canvas 需要 WxH，如 1920x1080，得到 {canvas}");
        proj.CanvasWidth = w; proj.CanvasHeight = h;
    }

    private static SymbolProject LoadProject(string path)
    {
        if (!File.Exists(path)) throw new IOException($"工程文件不存在: {path}");
        string json = File.ReadAllText(path, Encoding.UTF8);
        var proj = SymbolProject.Deserialize(json)
                   ?? throw new FormatException($"工程文件无法解析: {path}");
        proj.Primitives ??= new List<PrimitiveItemDto>();
        return proj;
    }

    private static void WriteProject(string path, SymbolProject proj)
        => File.WriteAllText(path, proj.Serialize(), new UTF8Encoding(false));

    private static CliException ValidationFail(string cmd, ValidationResult v)
    {
        foreach (var e in v.Errors) Console.Error.WriteLine("[error] " + e);
        foreach (var w in v.Warnings) Console.Error.WriteLine("[warn] " + w);
        return new CliException(2, $"`{cmd}` 因 {v.Errors.Count} 个校验错误而中止（退出码 2）");
    }

    private static void PrintHelp()
    {
        Console.Error.WriteLine("""
symbol-designer sym — 用结构化 JSON 生成/追加/检查 .sym.json 工程（供外部 AI-agent 画图）

用法:
  sym new       [--dsl <file|->] [--canvas WxH] [--output <path>] [--json]     新建工程(缺省空工程)
  sym add       <project.sym.json> --dsl <file|-> [--output <path>] [--json]  向已有工程追加图元
  sym info      <project.sym.json> [--json]                                   查看工程图元清单
  sym validate  [--dsl <file|->] [<project.sym.json>] [--json]                 校验 DSL/工程合法性
  sym render    <project.sym.json> [--output <png>] [--size WxH] [--json]      渲染离屏 PNG 预览(视觉自检)
  sym edit      <project.sym.json> --id <图元id> --set field=value [...]        按 id 修改图元字段(可多次 --set)
  sym remove    <project.sym.json> --id <图元id> [--output <p>] [--json]        按 id 删除图元
  sym help

选项:
  --dsl -        从 stdin 读 DSL JSON(管道流水)
  --json         机器可读结果输出到 stdout(人类日志走 stderr)
退出码: 0 成功 | 1 参数错 | 2 校验失败 | 3 IO 错 | 10 未捕获异常

DSL JSON 示例:
  { "canvasWidth":1920, "canvasHeight":1080,
    "primitives":[
      {"type":"rect","x":100,"y":100,"width":120,"height":60},
      {"type":"ellipse","centerX":400,"centerY":200,"radius":50},
      {"type":"line","points":[[50,60],[90,120]]},
      {"type":"polyline","points":[[0,0],[30,20],[60,0]],"closed":false},
      {"type":"bandPolyline","points":[[0,0],[40,0],[40,60]],"thickness":4},
      {"type":"ring","centerX":600,"centerY":200,"radius":40,"innerRadius":20},
      {"type":"pie","centerX":500,"centerY":400,"radius":30,"startAngle":0,"sweepAngle":120}
    ]}
type 别名: rect/roundedRect/ellipse/ring/triangle/line/polyline/bandPolyline/pie
""");
    }
}

/// <summary>带退出码的异常，失败中断当前子命令。</summary>
internal sealed class CliException : Exception
{
    public int Code { get; }
    public CliException(int code, string message) : base(message) => Code = code;
}