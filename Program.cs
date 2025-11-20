using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace CodeAnalyzer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== VB.NET Code Analyzer (med Roslyn) ===\n");
            
            string path = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
            
            if (!Directory.Exists(path))
            {
                Console.WriteLine($"Katalogen finns inte: {path}");
                return;
            }

            Console.WriteLine($"Analyserar katalog: {path}\n");
            
            var files = Directory.GetFiles(path, "*.vb", SearchOption.AllDirectories);
            var allMethodCalls = new Dictionary<string, List<MethodCallInfo>>();
            
            foreach (var file in files)
            {
                AnalyzeFile(file, allMethodCalls);
            }

            // Skriv ut sammanfattning av metodanrop
            Console.WriteLine("\n=== SAMMANFATTNING AV METODANROP ===");
            var sortedMethods = allMethodCalls
                .OrderByDescending(kvp => kvp.Value.Count)
                .Take(20);

            foreach (var method in sortedMethods)
            {
                Console.WriteLine($"\n{method.Key} - Anropas {method.Value.Count} gånger:");
                foreach (var call in method.Value.Take(5))
                {
                    Console.WriteLine($"  - {call.FileName}:{call.LineNumber}");
                }
                if (method.Value.Count > 5)
                {
                    Console.WriteLine($"  ... och {method.Value.Count - 5} fler");
                }
            }
            
            Console.WriteLine("\n\nAnalys klar!");
        }

        static void AnalyzeFile(string filePath, Dictionary<string, List<MethodCallInfo>> allMethodCalls)
        {
            Console.WriteLine($"\n--- Fil: {Path.GetFileName(filePath)} ---");
            
            var code = File.ReadAllText(filePath);
            var tree = VisualBasicSyntaxTree.ParseText(code);
            var root = tree.GetRoot();

            var dependencies = new HashSet<string>();
            var sqlCalls = new List<SqlCallInfo>();
            var methodCalls = new List<MethodCallInfo>();

            // Analysera Imports
            var imports = root.DescendantNodes().OfType<ImportsStatementSyntax>();
            foreach (var import in imports)
            {
                foreach (var clause in import.ImportsClauses)
                {
                    if (clause is SimpleImportsClauseSyntax simpleImport)
                    {
                        dependencies.Add(simpleImport.Name.ToString());
                    }
                }
            }

            // Hitta alla metodanrop
            var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
            foreach (var invocation in invocations)
            {
                var methodName = GetFullMethodName(invocation);
                var lineNumber = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1;
                
                var callInfo = new MethodCallInfo
                {
                    MethodName = methodName,
                    FileName = Path.GetFileName(filePath),
                    FilePath = filePath,
                    LineNumber = lineNumber
                };
                
                methodCalls.Add(callInfo);
                
                // Lägg till i global lista
                if (!allMethodCalls.ContainsKey(methodName))
                {
                    allMethodCalls[methodName] = new List<MethodCallInfo>();
                }
                allMethodCalls[methodName].Add(callInfo);

                // Kolla SQL-relaterade anrop
                if (IsSqlExecuteMethod(methodName))
                {
                    if (invocation.ArgumentList != null)
                    {
                        foreach (var arg in invocation.ArgumentList.Arguments)
                        {
                            var value = GetStringValue(arg.GetExpression());
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                sqlCalls.Add(new SqlCallInfo
                                {
                                    Type = methodName,
                                    SqlText = value,
                                    LineNumber = lineNumber
                                });
                            }
                        }
                    }
                }
            }

            // Hitta SQL CommandText tilldelningar
            var assignments = root.DescendantNodes().OfType<AssignmentStatementSyntax>();
            foreach (var assignment in assignments)
            {
                if (assignment.Left.ToString().Contains("CommandText"))
                {
                    var value = GetStringValue(assignment.Right);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        sqlCalls.Add(new SqlCallInfo
                        {
                            Type = "CommandText",
                            SqlText = value,
                            LineNumber = tree.GetLineSpan(assignment.Span).StartLinePosition.Line + 1
                        });
                    }
                }
            }

            // Hitta SqlCommand konstruktorer
            var objectCreations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();
            foreach (var creation in objectCreations)
            {
                var typeName = creation.Type.ToString();
                if (typeName.Contains("SqlCommand") || typeName.Contains("OleDbCommand"))
                {
                    dependencies.Add("System.Data.SqlClient");
                    
                    if (creation.ArgumentList != null && creation.ArgumentList.Arguments.Count > 0)
                    {
                        var value = GetStringValue(creation.ArgumentList.Arguments[0].GetExpression());
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            sqlCalls.Add(new SqlCallInfo
                            {
                                Type = "SqlCommand Constructor",
                                SqlText = value,
                                LineNumber = tree.GetLineSpan(creation.Span).StartLinePosition.Line + 1
                            });
                        }
                    }
                }
            }

            // Skriv ut resultat för denna fil
            if (dependencies.Any())
            {
                Console.WriteLine("\nBeroenden:");
                foreach (var dep in dependencies.OrderBy(d => d))
                {
                    Console.WriteLine($"  - {dep}");
                }
            }

            Console.WriteLine($"\nMetodanrop i denna fil: {methodCalls.Count}");
            var topMethods = methodCalls
                .GroupBy(m => m.MethodName)
                .OrderByDescending(g => g.Count())
                .Take(5);
            
            foreach (var group in topMethods)
            {
                Console.WriteLine($"  - {group.Key}: {group.Count()} anrop");
            }

            if (sqlCalls.Any())
            {
                Console.WriteLine("\nSQL-anrop:");
                foreach (var call in sqlCalls)
                {
                    Console.WriteLine($"\n  Rad {call.LineNumber} - {call.Type}:");
                    var truncated = call.SqlText.Length > 100 
                        ? call.SqlText.Substring(0, 100) + "..." 
                        : call.SqlText;
                    Console.WriteLine($"    {truncated}");
                    
                    if (call.SqlText.IndexOf("EXEC", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        call.SqlText.IndexOf("EXECUTE", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Console.WriteLine($"    ⚠️  Innehåller EXEC/EXECUTE");
                    }
                    
                    if (call.SqlText.StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"    📌 Stored Procedure: {call.SqlText.Split(' ', '(')[0]}");
                    }
                }
            }
        }

        static string GetStringValue(SyntaxNode node)
        {
            if (node is LiteralExpressionSyntax literal)
            {
                return literal.Token.ValueText;
            }
            
            if (node is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.ConcatenateExpression))
            {
                var left = GetStringValue(binary.Left);
                var right = GetStringValue(binary.Right);
                return left + right;
            }
            
            return string.Empty;
        }

        static string GetFullMethodName(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var obj = memberAccess.Expression.ToString();
                var method = memberAccess.Name.ToString();
                return $"{obj}.{method}";
            }
            return invocation.Expression.ToString();
        }

        static bool IsSqlExecuteMethod(string methodName)
        {
            var sqlMethods = new[] 
            { 
                "ExecuteReader", "ExecuteNonQuery", "ExecuteScalar", 
                "Execute", "ExecuteQuery" 
            };  
            
            return sqlMethods.Any(m => methodName.Contains(m));
        }
    }

    class SqlCallInfo
    {
        public string Type { get; set; }
        public string SqlText { get; set; }
        public int LineNumber { get; set; }
    }

    class MethodCallInfo
    {
        public string MethodName { get; set; }
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
    }  
