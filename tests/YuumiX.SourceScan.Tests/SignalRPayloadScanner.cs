using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using YuumiX.Core.Serialization;

namespace YuumiX.SourceScan.Tests;

/// <summary>按实际 SignalR 符号检查载荷元数据，覆盖类型推断、非泛型发送与跨行调用。</summary>
internal static class SignalRPayloadScanner
{
    internal sealed record Violation(string File, int Line, string Detail);

    private static readonly HashSet<string> PayloadMethods = new(StringComparer.Ordinal)
    {
        "On", "SendAsync", "InvokeAsync", "SendCoreAsync", "InvokeCoreAsync",
        "StreamAsync", "StreamAsChannelAsync", "StreamAsyncCore", "StreamAsChannelCoreAsync",
    };

    private static readonly MetadataReference[] References =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("无法读取测试进程的程序集引用"))
        .Split(Path.PathSeparator)
        .Append(typeof(AotJson).Assembly.Location)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    internal static IReadOnlyList<Violation> Scan(IEnumerable<(string Path, string Source)> sources)
    {
        var trees = sources.Select(source => CSharpSyntaxTree.ParseText(source.Source, path: source.Path)).ToArray();
        var globalUsings = CSharpSyntaxTree.ParseText("""
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            """);
        var compilation = CSharpCompilation.Create("SignalRPayloadGate", [.. trees, globalUsings], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var violations = new List<Violation>();

        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = call.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    MemberBindingExpressionSyntax member => member.Name.Identifier.ValueText,
                    SimpleNameSyntax simple => simple.Identifier.ValueText,
                    _ => "",
                };
                if (!PayloadMethods.Contains(name))
                {
                    continue;
                }

                if (model.GetOperation(call) is not IInvocationOperation invocation)
                {
                    // 已知 HubConnection 上无法解析的调用不能当成安全路径放行。
                    if (call.Expression is MemberAccessExpressionSyntax member &&
                        model.GetTypeInfo(member.Expression).Type?.ToDisplayString() == "Microsoft.AspNetCore.SignalR.Client.HubConnection")
                    {
                        Report("无法解析 SignalR 调用，请先修复编译错误");
                    }
                    continue;
                }

                var method = invocation.TargetMethod;
                if (method.ContainingNamespace.ToDisplayString() != "Microsoft.AspNetCore.SignalR.Client")
                {
                    continue;
                }

                foreach (var type in method.TypeArguments)
                {
                    CheckType(type);
                }

                foreach (var argument in invocation.Arguments)
                {
                    if (argument.Parameter?.Name is "parameterTypes" or "returnType" ||
                        argument.Parameter?.Name == "args")
                    {
                        // Type[] / object[] 变量无法证明其运行时内容，要求改用可检查的类型化重载。
                        Report("动态参数/返回类型不可静态验证，请使用类型化重载并补充反射关闭验收");
                    }
                    else if (argument.Parameter?.Type.SpecialType == SpecialType.System_Object)
                    {
                        var value = argument.Value;
                        while (value is IConversionOperation { IsImplicit: true } conversion)
                        {
                            value = conversion.Operand;
                        }
                        if (!(value.ConstantValue.HasValue && value.ConstantValue.Value is null))
                        {
                            CheckType(value.Type);
                        }
                    }
                }

                void CheckType(ITypeSymbol? symbol)
                {
                    var type = ResolveType(symbol);
                    if (type is null || type == typeof(object))
                    {
                        Report($"无法确定载荷实际类型：{symbol}");
                        return;
                    }
                    try
                    {
                        AotJson.Options.GetTypeInfo(type);
                    }
                    catch (NotSupportedException)
                    {
                        Report($"CoreJsonContext 缺少载荷元数据：{symbol}");
                    }
                }

                void Report(string detail) => violations.Add(new Violation(tree.FilePath,
                    tree.GetLineSpan(call.Span).StartLinePosition.Line + 1, detail));
            }
        }

        return violations;
    }

    private static Type? ResolveType(ITypeSymbol? symbol)
    {
        if (symbol is IArrayTypeSymbol { Rank: 1 } array)
        {
            return ResolveType(array.ElementType)?.MakeArrayType();
        }
        if (symbol is not INamedTypeSymbol { TypeKind: not TypeKind.Error } named)
        {
            return null;
        }

        var name = named.MetadataName;
        for (var parent = named.ContainingType; parent is not null; parent = parent.ContainingType)
        {
            name = parent.MetadataName + "+" + name;
        }
        if (!named.ContainingNamespace.IsGlobalNamespace)
        {
            name = named.ContainingNamespace.ToDisplayString() + "." + name;
        }
        var type = typeof(AotJson).Assembly.GetType(name)
            ?? Type.GetType($"{name}, {named.ContainingAssembly.Identity.Name}", throwOnError: false);
        if (type is null || !named.IsGenericType)
        {
            return type;
        }

        var arguments = new List<Type>();
        foreach (var argument in named.TypeArguments)
        {
            if (ResolveType(argument) is not { } argumentType)
            {
                return null;
            }
            arguments.Add(argumentType);
        }
        return type.MakeGenericType(arguments.ToArray());
    }
}
