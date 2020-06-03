using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace CfgTest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Attempt to set the version of MSBuild.
            var visualStudioInstances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
            var instance = visualStudioInstances.Length == 1
                // If there is only one instance of MSBuild on this machine, set that as the one to use.
                ? visualStudioInstances[0]
                // Handle selecting the version of MSBuild you want to use.
                : SelectVisualStudioInstance(visualStudioInstances);

            Console.WriteLine($"Using MSBuild at '{instance.MSBuildPath}' to load projects.");

            // NOTE: Be sure to register an instance with the MSBuildLocator 
            //       before calling MSBuildWorkspace.Create()
            //       otherwise, MSBuildWorkspace won't MEF compose.
            MSBuildLocator.RegisterInstance(instance);

            using (var workspace = MSBuildWorkspace.Create())
            {
                // Print message for WorkspaceFailed event to help diagnosing project load failures.
                workspace.WorkspaceFailed += (o, e) => Console.WriteLine(e.Diagnostic.Message);

                var solutionPath = args[0];
                Console.WriteLine($"Loading solution '{solutionPath}'");

                // Attach progress reporter so we print projects as they are loaded.
                var project = await workspace.OpenProjectAsync(solutionPath, new ConsoleProgressReporter());
                Console.WriteLine($"Finished loading solution '{solutionPath}'");

                await AnalyzeAsync(project);
            }
        }

        private static async Task AnalyzeAsync(Project project)
        {
            var compilation = await project.GetCompilationAsync();
            var mainMethod = compilation.GetEntryPoint(default);
            var mainSyntax = mainMethod.DeclaringSyntaxReferences.Single();
            var syntax = (MethodDeclarationSyntax)await mainSyntax.GetSyntaxAsync();
            var semanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);
            var cfg = ControlFlowGraph.Create(syntax, semanticModel);

            var bodyOperation = semanticModel.GetOperation(syntax.Body);

            var callOperation = bodyOperation.Descendants()
                                             .OfType<IInvocationOperation>()
                                             .Single(o => o.TargetMethod.Name == "WindowsApi");

            var statement = callOperation.Syntax.AncestorsAndSelf()
                                                .OfType<StatementSyntax>()
                                                .Select(s => semanticModel.GetOperation(s))
                                                .First();

            var basicBlock = cfg.Blocks.Single(bb => bb.Operations.Any(o => o.Syntax == statement.Syntax));


            var platformCheck = PlatformCheckPredicatePass.Instance.Analyze(cfg, basicBlock);

            if (!platformCheck.IsNegated && platformCheck.Platform == nameof(OSPlatform.Windows))
                Console.WriteLine("All good");
            else
                Console.WriteLine("Sorry, you must be on Windows");

            DumpCfg(cfg);
        }

        private static void DumpCfg(ControlFlowGraph cfg)
        {
            using (var writer = new StreamWriter(@"P:\minsk\cfg-roslyn.dot"))
            {
                string Quote(string text)
                {
                    return "\"" + text.TrimEnd().Replace("\\", "\\\\").Replace("\"", "\\\"").Replace(Environment.NewLine, "\\l") + "\\l\"";
                }

                writer.WriteLine("digraph G {");

                var branches = new HashSet<ControlFlowBranch>();

                foreach (var block in cfg.Blocks)
                {
                    branches.UnionWith(block.Predecessors);
                    if (block.ConditionalSuccessor != null)
                        branches.Add(block.ConditionalSuccessor);
                    if (block.FallThroughSuccessor != null)
                        branches.Add(block.FallThroughSuccessor);
                }

                void WriteRegion(ControlFlowRegion region)
                {
                    writer.WriteLine($"subgraph cluster_{region.FirstBlockOrdinal} {{");
                    writer.WriteLine($"    label = {Quote(region.Kind.ToString())}");
                    writer.WriteLine($"    fontcolor = blue");
                    writer.WriteLine($"    style = dashed");
                    writer.WriteLine($"    color = blue");

                    foreach (var block in cfg.Blocks.Where(b => b.EnclosingRegion == region))
                    {
                        var text = block.Ordinal.ToString() + Environment.NewLine + string.Join(Environment.NewLine, block.Operations.Select(o => GetString(o)));
                        var id = block.Ordinal.ToString();
                        var label = Quote(text);
                        var style = block.IsReachable ? "solid" : "dotted";
                        writer.WriteLine($"    {id} [label = {label}, shape = box, style = {style}]");
                    }

                    foreach (var child in region.NestedRegions)
                    {
                        WriteRegion(child);
                    }

                    writer.WriteLine("}");
                }

                WriteRegion(cfg.Root);

                foreach (var branch in branches)
                {
                    if (branch.Source == null || branch.Destination == null)
                        continue;

                    var fromId = branch.Source.Ordinal.ToString();
                    var toId = branch.Destination.Ordinal.ToString();

                    var label = "";

                    if (branch.IsConditionalSuccessor)
                    {
                        if (branch.Source.ConditionKind != ControlFlowConditionKind.None)
                            label += "[" + branch.Source.ConditionKind + "]" + Environment.NewLine;
                    }
                    else if (branch.Source.FallThroughSuccessor == branch)
                    {
                        var negated = branch.Source.ConditionKind == ControlFlowConditionKind.WhenTrue;
                        if (negated)
                            label += "[WhenFalse]" + Environment.NewLine;
                    }

                    if (branch.Source.BranchValue != null)
                        label += " " + GetString(branch.Source.BranchValue);

                    var quotedLabel = Quote(label.Trim());
                    writer.WriteLine($"    {fromId} -> {toId} [label = {quotedLabel}]");
                }

                writer.WriteLine("}");
            }
        }

        private static string GetString(IOperation operation)
        {
            using (var stringWriter = new StringWriter())
            using (var writer = new IndentedTextWriter(stringWriter, "    "))
            {
                writer.WriteLine("// " + operation.Syntax.ToString());
                GetString(operation, writer);
                writer.Flush();
                return stringWriter.ToString();
            }
        }

        private static void GetString(IOperation operation, IndentedTextWriter writer)
        {
            writer.Write(operation.Kind.ToString());

            if (operation is ILocalReferenceOperation l)
            {
                writer.Write(" " + l.Local.Name);
            }
            else if (operation is IFlowCaptureOperation fc)
            {
                writer.Write(" " + GetValue(fc.Id));
            }
            else if (operation is IFlowCaptureReferenceOperation fcr)
            {
                writer.Write(" " + GetValue(fcr.Id));
            }
            else if (operation is IInvocationOperation i)
            {
                writer.Write(" " + i.TargetMethod.Name);
            }
            else if (operation is IPropertyReferenceOperation p)
            {
                writer.Write(" " + p.Property.Name);                
            }
            else if (operation is IFieldReferenceOperation f)
            {
                writer.Write(" " + f.Field.Name);
            }
            else if (operation is IEventReferenceOperation e)
            {
                writer.Write(" " + e.Event.Name);
            }
            else if (operation is IConversionOperation c)
            {
                writer.Write(" " + c.Type.Name);
            }

            writer.WriteLine();

            writer.Indent++;

            foreach (var child in operation.Children)
                GetString(child, writer);

            writer.Indent--;
        }

        private static int GetValue(CaptureId captureId)
        {
            var p = typeof(CaptureId).GetProperty("Value", BindingFlags.Instance | BindingFlags.NonPublic);
            return (int)p.GetValue(captureId);
        }

        private static VisualStudioInstance SelectVisualStudioInstance(VisualStudioInstance[] visualStudioInstances)
        {
            Console.WriteLine("Multiple installs of MSBuild detected please select one:");
            for (int i = 0; i < visualStudioInstances.Length; i++)
            {
                Console.WriteLine($"Instance {i + 1}");
                Console.WriteLine($"    Name: {visualStudioInstances[i].Name}");
                Console.WriteLine($"    Version: {visualStudioInstances[i].Version}");
                Console.WriteLine($"    MSBuild Path: {visualStudioInstances[i].MSBuildPath}");
            }

            while (true)
            {
                var userResponse = Console.ReadLine();
                if (int.TryParse(userResponse, out int instanceNumber) &&
                    instanceNumber > 0 &&
                    instanceNumber <= visualStudioInstances.Length)
                {
                    return visualStudioInstances[instanceNumber - 1];
                }
                Console.WriteLine("Input not accepted, try again.");
            }
        }

        private class ConsoleProgressReporter : IProgress<ProjectLoadProgress>
        {
            public void Report(ProjectLoadProgress loadProgress)
            {
                var projectDisplay = Path.GetFileName(loadProgress.FilePath);
                if (loadProgress.TargetFramework != null)
                {
                    projectDisplay += $" ({loadProgress.TargetFramework})";
                }

                Console.WriteLine($"{loadProgress.Operation,-15} {loadProgress.ElapsedTime,-15:m\\:ss\\.fffffff} {projectDisplay}");
            }
        }
    }

    internal abstract class PredicatePass<TState>
    {
        public abstract TState CreateEmptyState();
        public abstract TState CreateState(bool isNegated, IOperation condition);
        public abstract TState And(TState state1, TState state2);
        public abstract TState Or(TState state1, TState state2);

        public TState Analyze(ControlFlowGraph graph, BasicBlock basicBlock)
        {
            var processedBlocks = new HashSet<BasicBlock>();

            TState Process(BasicBlock block)
            {
                var state = CreateEmptyState();

                if (processedBlocks.Add(block))
                {
                    var isFirst = true;

                    foreach (var predecessor in block.Predecessors)
                    {
                        // Ignore back pointers
                        if (processedBlocks.Contains(predecessor.Source))
                            continue;

                        var edgeState = Process(predecessor.Source);

                        if (predecessor.Source.BranchValue != null)
                        {
                            var negated = predecessor.Source.ConditionalSuccessor == predecessor &&
                                          predecessor.Source.ConditionKind == ControlFlowConditionKind.WhenFalse ||
                                          predecessor.Source.FallThroughSuccessor == predecessor &&
                                          predecessor.Source.ConditionKind == ControlFlowConditionKind.WhenTrue;
                            var conditionState = CreateState(negated, predecessor.Source.BranchValue);
                            edgeState = And(edgeState, conditionState);
                        }

                        if (isFirst)
                        {
                            state = edgeState;
                            isFirst = false;
                        }
                        else
                        {
                            state = Or(state, edgeState);
                        }
                    }
                }

                return state;
            }

            return Process(basicBlock);
        }
    }

    internal sealed class PlatformCheckPredicatePass : PredicatePass<PlatformCheckResult>
    {
        public static PlatformCheckPredicatePass Instance { get; } = new PlatformCheckPredicatePass();

        private PlatformCheckPredicatePass()
        {
        }

        public override PlatformCheckResult CreateEmptyState()
        {
            return PlatformCheckResult.None;
        }

        public override PlatformCheckResult CreateState(bool isNegated, IOperation condition)
        {
            return PlatformChecker.Check(isNegated, condition);
        }

        public override PlatformCheckResult And(PlatformCheckResult state1, PlatformCheckResult state2)
        {
            return PlatformCheckResult.And(state1, state2);
        }

        public override PlatformCheckResult Or(PlatformCheckResult state1, PlatformCheckResult state2)
        {
            return PlatformCheckResult.None;
        }
    }

    internal struct PlatformCheckResult
    {
        public static PlatformCheckResult None { get; } = default;
        public static PlatformCheckResult Create(string platform) => new PlatformCheckResult(platform, false);

        public static PlatformCheckResult Negate(PlatformCheckResult result)
        {
            if (result.IsNone)
                return result;

            return new PlatformCheckResult(result.Platform, !result.IsNegated);
        }

        public static PlatformCheckResult And(PlatformCheckResult left, PlatformCheckResult right)
        {
            if (left.IsNone)
                return right;

            if (right.IsNone)
                return left;

            if (left.IsNegated == right.IsNegated &&
                left.Platform == right.Platform)
                return left;

            return None;
        }


        public PlatformCheckResult(string platform, bool isNegated)
        {
            Platform = platform;
            IsNegated = isNegated;
        }

        public bool IsNone => Platform == null;

        public bool IsNegated { get; }
        
        public string Platform { get; }
    }

    internal sealed class PlatformChecker : OperationVisitor<object, PlatformCheckResult>
    {
        private static readonly PlatformChecker _instance = new PlatformChecker();

        public static PlatformCheckResult Check(IEnumerable<(bool IsNegated, IOperation Condition)> operations)
        {
            var result = PlatformCheckResult.None;

            foreach (var (isNegated, condition) in operations)
            {
                var conditionResult = Check(isNegated, condition);
                result = PlatformCheckResult.And(result, conditionResult);
            }

            return result;
        }

        public static PlatformCheckResult Check(bool isNegated, IOperation condition)
        {
            var result = _instance.Visit(condition, null);
            if (isNegated)
                result = PlatformCheckResult.Negate(result);

            return result;
        }

        public override PlatformCheckResult VisitUnaryOperator(IUnaryOperation operation, object argument)
        {
            if (operation.OperatorKind == UnaryOperatorKind.Not ||
                operation.OperatorKind == UnaryOperatorKind.BitwiseNegation)
            {
                var operand = Visit(operation.Operand, argument);
                return PlatformCheckResult.Negate(operand);
            }

            return PlatformCheckResult.None;
        }

        public override PlatformCheckResult VisitBinaryOperator(IBinaryOperation operation, object argument)
        {
            if (operation.OperatorKind == BinaryOperatorKind.ConditionalAnd ||
                operation.OperatorKind == BinaryOperatorKind.And)
            {
                var left = Visit(operation.LeftOperand, argument);
                var right = Visit(operation.RightOperand, argument);
                return PlatformCheckResult.And(left, right);
            }

            return PlatformCheckResult.None;
        }

        public override PlatformCheckResult VisitInvocation(IInvocationOperation operation, object argument)
        {
            if (operation.TargetMethod.Name == "IsOSPlatform" &&
                operation.Arguments.Length == 1 &&
                operation.Arguments[0].Value is IPropertyReferenceOperation p)
            {
                var platform = p.Property.Name;
                return PlatformCheckResult.Create(platform);
            }

            return PlatformCheckResult.None;
        }
    }
}
