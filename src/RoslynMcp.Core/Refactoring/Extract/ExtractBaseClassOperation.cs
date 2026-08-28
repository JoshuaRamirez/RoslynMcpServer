using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Extracts members to a new base class.
/// </summary>
public sealed class ExtractBaseClassOperation : RefactoringOperationBase<ExtractBaseClassParams>
{
    /// <summary>
    /// Creates a new extract base class operation.
    /// </summary>
    public ExtractBaseClassOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ExtractBaseClassParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (string.IsNullOrWhiteSpace(@params.BaseClassName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "baseClassName is required.");

        if (@params.Members == null || @params.Members.Count == 0)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "members is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!IsValidIdentifier(@params.BaseClassName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid base class name: {@params.BaseClassName}");

        if (@params.TargetFile != null)
        {
            if (!PathResolver.IsAbsolutePath(@params.TargetFile))
                throw new RefactoringException(ErrorCodes.InvalidTargetPath, "targetFile must be an absolute path.");

            if (!PathResolver.IsValidCSharpFilePath(@params.TargetFile))
                throw new RefactoringException(ErrorCodes.InvalidTargetPath, "targetFile must be a .cs file.");
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ExtractBaseClassParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find the type declaration
        var typeDeclaration = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == @params.TypeName);

        if (typeDeclaration == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Class '{@params.TypeName}' not found in file.");
        }

        // Get the type symbol
        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");
        }

        // Check if type already has a base class other than Object
        if (typeSymbol.BaseType != null &&
            typeSymbol.BaseType.SpecialType != SpecialType.System_Object)
        {
            throw new RefactoringException(
                ErrorCodes.TypeAlreadyHasBase,
                $"Type '{@params.TypeName}' already has base class '{typeSymbol.BaseType.Name}'.");
        }

        // Check if base class name already exists
        var existingType = await TypeResolver.FindTypeByNameAsync(
            $"{typeSymbol.ContainingNamespace}.{@params.BaseClassName}",
            cancellationToken);

        if (existingType != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Type '{@params.BaseClassName}' already exists.");
        }

        // Find members to extract
        var membersToExtract = FindMembersToExtract(typeDeclaration, @params.Members, semanticModel);

        // Generate base class
        var baseClass = GenerateBaseClass(
            @params.BaseClassName,
            membersToExtract,
            @params.MakeAbstract);

        // Get namespace
        var namespaceName = typeSymbol.ContainingNamespace?.ToDisplayString();

        // Explicit targetFile always wins. separateFile=true with no targetFile
        // writes {BaseClassName}.cs next to the source.
        var targetFile = ResolveTargetFile(@params);
        var isNewFile = targetFile != @params.SourceFile;

        if (isNewFile && typeSymbol.ContainingType != null)
        {
            throw new RefactoringException(
                ErrorCodes.CannotExtractNestedToSeparateFile,
                $"Cannot extract a base class for nested type '{@params.TypeName}' to a separate file. Extract in the source file instead.");
        }

        ThrowIfSiblingTargetExists(@params, targetFile);

        string? projectPath = null;
        string? updatedProjectText = null;
        if (isNewFile)
        {
            (projectPath, updatedProjectText) = PrepareExplicitCompileItemUpdate(document.Project, targetFile);
        }

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(
                operationId,
                @params,
                membersToExtract,
                baseClass,
                namespaceName,
                targetFile,
                updatedProjectText != null ? projectPath : null);
        }

        // Apply changes
        Solution newSolution;
        if (targetFile != @params.SourceFile)
        {
            // Create new file with base class
            newSolution = await CreateBaseClassInNewFileAsync(
                document.Project.Solution,
                document.Project,
                targetFile,
                baseClass,
                namespaceName,
                root,
                cancellationToken);
        }
        else
        {
            // Add base class to same file
            newSolution = AddBaseClassToSameFile(
                document,
                root,
                typeDeclaration,
                baseClass);
        }

        // Update derived class: remove extracted members and add base class
        var updatedDoc = newSolution.GetDocument(document.Id);
        if (updatedDoc != null)
        {
            var updatedRoot = await updatedDoc.GetSyntaxRootAsync(cancellationToken);
            if (updatedRoot != null)
            {
                var updatedTypeDecl = updatedRoot.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .First(t => t.Identifier.Text == @params.TypeName);

                // Add base class to type
                var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(@params.BaseClassName));

                ClassDeclarationSyntax newTypeDecl;
                if (updatedTypeDecl.BaseList == null)
                {
                    newTypeDecl = updatedTypeDecl.WithBaseList(
                        SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseType)));
                }
                else
                {
                    // Insert base class before interfaces
                    var newBaseList = SyntaxFactory.BaseList(
                        SyntaxFactory.SeparatedList(
                            new[] { baseType }.Concat(updatedTypeDecl.BaseList.Types)));
                    newTypeDecl = updatedTypeDecl.WithBaseList(newBaseList);
                }

                // Remove extracted members from derived class. Multi-variable
                // event fields drop only the selected declarators. Indexers
                // match by parameter-list signature so Item / this[] /
                // this[int i] all drop the selected indexer only.
                var memberNames = @params.Members.ToHashSet();
                foreach (var extracted in membersToExtract)
                {
                    if (extracted is IndexerDeclarationSyntax indexer)
                        memberNames.Add(GetIndexerRemovalKey(indexer));
                }

                var newMembers = RebuildDerivedMembers(newTypeDecl.Members, memberNames);

                newTypeDecl = newTypeDecl.WithMembers(SyntaxFactory.List(newMembers));

                updatedRoot = updatedRoot.ReplaceNode(updatedTypeDecl, newTypeDecl);
                newSolution = updatedDoc.WithSyntaxRoot(updatedRoot).Project.Solution;
            }
        }

        // Commit changes
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        var filesModified = commitResult.FilesModified.ToList();
        if (updatedProjectText != null && !string.IsNullOrWhiteSpace(projectPath))
        {
            WriteProjectFile(projectPath, updatedProjectText);
            if (!filesModified.Contains(projectPath, StringComparer.OrdinalIgnoreCase))
                filesModified.Add(projectPath);
        }

        return RefactoringResult.Succeeded(
            operationId,
            new FileChanges
            {
                FilesModified = filesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            new Contracts.Models.SymbolInfo
            {
                Name = @params.BaseClassName,
                FullyQualifiedName = string.IsNullOrEmpty(namespaceName)
                    ? @params.BaseClassName
                    : $"{namespaceName}.{@params.BaseClassName}",
                Kind = Contracts.Enums.SymbolKind.Class
            },
            0,
            0);
    }

    private static List<MemberDeclarationSyntax> FindMembersToExtract(
        ClassDeclarationSyntax typeDeclaration,
        IReadOnlyList<string> memberNames,
        SemanticModel semanticModel)
    {
        var requestedSet = new HashSet<string>(memberNames);
        var unmatched = new HashSet<string>(memberNames);
        var result = new List<MemberDeclarationSyntax>();

        foreach (var member in typeDeclaration.Members)
        {
            if (member is EventFieldDeclarationSyntax eventField)
            {
                var selected = eventField.Declaration.Variables
                    .Where(v => requestedSet.Contains(v.Identifier.Text))
                    .ToList();
                if (selected.Count == 0)
                    continue;

                foreach (var variable in selected)
                    unmatched.Remove(variable.Identifier.Text);

                result.Add(eventField.WithDeclaration(
                    eventField.Declaration.WithVariables(SyntaxFactory.SeparatedList(selected))));
                continue;
            }

            // Indexers match metadata name (Item), Roslyn name (this[]), and
            // conventional display (this[int i]) — same identity forms as
            // implement_interface / extract_interface.
            if (member is IndexerDeclarationSyntax indexerDecl
                && semanticModel.GetDeclaredSymbol(indexerDecl) is IPropertySymbol { IsIndexer: true } indexer
                && ImplementInterfaceOperation.MatchesRequestedMember(indexer, requestedSet))
            {
                result.Add(member);
                foreach (var requested in unmatched.ToList())
                {
                    if (ImplementInterfaceOperation.MatchesRequestedMember(
                            indexer, new HashSet<string> { requested }))
                    {
                        unmatched.Remove(requested);
                    }
                }

                continue;
            }

            var name = GetMemberName(member);
            if (name != null && requestedSet.Contains(name))
            {
                result.Add(member);
                unmatched.Remove(name);
            }
        }

        if (unmatched.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotFound,
                $"Members not found: {string.Join(", ", unmatched)}");
        }

        return result;
    }

    private static string? GetMemberName(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            IndexerDeclarationSyntax => "this[]",
            FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
            EventDeclarationSyntax e => e.Identifier.Text,
            EventFieldDeclarationSyntax ef => ef.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
            _ => null
        };
    }

    /// <summary>
    /// Stable key for dropping a selected indexer from the derived type.
    /// Parameter types and <see cref="RefKind"/> distinguish overloads so
    /// <c>this[]</c> / <c>Item</c> do not remove an unselected indexer.
    /// </summary>
    private static string GetIndexerRemovalKey(IndexerDeclarationSyntax indexer)
    {
        var parts = indexer.ParameterList.Parameters.Select(parameter =>
        {
            var type = parameter.Type?.ToString() ?? string.Empty;
            if (parameter.Modifiers.Any(SyntaxKind.RefKeyword)
                && parameter.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
            {
                return "ref readonly " + type;
            }

            if (parameter.Modifiers.Any(SyntaxKind.RefKeyword))
                return "ref " + type;
            if (parameter.Modifiers.Any(SyntaxKind.OutKeyword))
                return "out " + type;
            if (parameter.Modifiers.Any(SyntaxKind.InKeyword))
                return "in " + type;
            return type;
        });

        return "indexer:" + string.Join(",", parts);
    }

    private static IEnumerable<string> GetExtractedMemberNames(MemberDeclarationSyntax member)
    {
        if (member is EventFieldDeclarationSyntax eventField)
        {
            foreach (var variable in eventField.Declaration.Variables)
                yield return variable.Identifier.Text;
            yield break;
        }

        var name = GetMemberName(member);
        if (name != null)
            yield return name;
    }

    private static bool ShouldRemoveMember(MemberDeclarationSyntax member, HashSet<string> memberNames)
    {
        if (member is IndexerDeclarationSyntax indexer)
            return memberNames.Contains(GetIndexerRemovalKey(indexer));

        var name = GetMemberName(member);
        return name != null && memberNames.Contains(name);
    }

    /// <summary>
    /// Drops extracted members from the derived type. Field-like events match
    /// by declarator name so a multi-variable event field keeps unrelated
    /// declarators on the derived type.
    /// </summary>
    private static List<MemberDeclarationSyntax> RebuildDerivedMembers(
        SyntaxList<MemberDeclarationSyntax> members,
        HashSet<string> extractedNames)
    {
        var result = new List<MemberDeclarationSyntax>();
        foreach (var member in members)
        {
            if (member is EventFieldDeclarationSyntax eventField)
            {
                var remaining = eventField.Declaration.Variables
                    .Where(v => !extractedNames.Contains(v.Identifier.Text))
                    .ToList();
                if (remaining.Count == eventField.Declaration.Variables.Count)
                {
                    result.Add(member);
                    continue;
                }

                if (remaining.Count == 0)
                    continue;

                result.Add(eventField.WithDeclaration(
                    eventField.Declaration.WithVariables(SyntaxFactory.SeparatedList(remaining))));
                continue;
            }

            if (!ShouldRemoveMember(member, extractedNames))
                result.Add(member);
        }

        return result;
    }

    private static ClassDeclarationSyntax GenerateBaseClass(
        string className,
        List<MemberDeclarationSyntax> members,
        bool makeAbstract)
    {
        var modifiers = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PublicKeyword) };
        if (makeAbstract)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));
        }

        // Make members protected if they're private
        var adjustedMembers = members.Select(m => AdjustMemberAccessibility(m)).ToList();

        return SyntaxFactory.ClassDeclaration(className)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithMembers(SyntaxFactory.List(adjustedMembers))
            .NormalizeWhitespace();
    }

    private static MemberDeclarationSyntax AdjustMemberAccessibility(MemberDeclarationSyntax member)
    {
        // If private, make protected
        var modifiers = member switch
        {
            MethodDeclarationSyntax m => m.Modifiers,
            PropertyDeclarationSyntax p => p.Modifiers,
            IndexerDeclarationSyntax i => i.Modifiers,
            FieldDeclarationSyntax f => f.Modifiers,
            EventDeclarationSyntax e => e.Modifiers,
            EventFieldDeclarationSyntax ef => ef.Modifiers,
            _ => default
        };

        if (modifiers.Any(SyntaxKind.PrivateKeyword))
        {
            var newModifiers = SyntaxFactory.TokenList(
                modifiers.Where(m => !m.IsKind(SyntaxKind.PrivateKeyword))
                         .Prepend(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword)));

            return member switch
            {
                MethodDeclarationSyntax m => m.WithModifiers(newModifiers),
                PropertyDeclarationSyntax p => p.WithModifiers(newModifiers),
                IndexerDeclarationSyntax i => i.WithModifiers(newModifiers),
                FieldDeclarationSyntax f => f.WithModifiers(newModifiers),
                EventDeclarationSyntax e => e.WithModifiers(newModifiers),
                EventFieldDeclarationSyntax ef => ef.WithModifiers(newModifiers),
                _ => member
            };
        }

        return member;
    }

    private Task<Solution> CreateBaseClassInNewFileAsync(
        Solution solution,
        Project project,
        string targetFile,
        ClassDeclarationSyntax baseClass,
        string? namespaceName,
        SyntaxNode sourceRoot,
        CancellationToken cancellationToken)
    {
        // Build compilation unit with usings from source
        var usings = sourceRoot.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .ToList();

        MemberDeclarationSyntax wrappedClass;
        if (!string.IsNullOrEmpty(namespaceName))
        {
            wrappedClass = SyntaxFactory.FileScopedNamespaceDeclaration(
                    SyntaxFactory.ParseName(namespaceName))
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(baseClass));
        }
        else
        {
            wrappedClass = baseClass;
        }

        var compilationUnit = SyntaxFactory.CompilationUnit()
            .WithUsings(SyntaxFactory.List(usings))
            .WithMembers(SyntaxFactory.SingletonList(wrappedClass))
            .NormalizeWhitespace();

        // Create new document
        var newDoc = project.AddDocument(
            Path.GetFileName(targetFile),
            compilationUnit,
            filePath: targetFile);

        return Task.FromResult(newDoc.Project.Solution);
    }

    private static Solution AddBaseClassToSameFile(
        Document document,
        SyntaxNode root,
        ClassDeclarationSyntax derivedClass,
        ClassDeclarationSyntax baseClass)
    {
        // Insert base class before derived class
        var newBaseClass = baseClass
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed);

        var newRoot = root.InsertNodesBefore(derivedClass, new[] { newBaseClass });
        var newDoc = document.WithSyntaxRoot(newRoot);

        return newDoc.Project.Solution;
    }

    /// <summary>
    /// Resolves the destination for the extracted base class.
    /// Explicit <see cref="ExtractBaseClassParams.TargetFile"/> always wins.
    /// </summary>
    private static string ResolveTargetFile(ExtractBaseClassParams @params)
    {
        if (!string.IsNullOrWhiteSpace(@params.TargetFile))
            return @params.TargetFile;

        if (!@params.SeparateFile)
            return @params.SourceFile;

        var directory = Path.GetDirectoryName(@params.SourceFile);
        if (string.IsNullOrEmpty(directory))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSourcePath,
                "sourceFile must have a parent directory.");
        }

        return PathResolver.Combine(directory, @params.BaseClassName + ".cs");
    }

    private static void ThrowIfSiblingTargetExists(ExtractBaseClassParams @params, string targetFile)
    {
        // Explicit targetFile keeps today's path; only the computed sibling is rejected.
        if (!string.IsNullOrWhiteSpace(@params.TargetFile) || !@params.SeparateFile)
            return;

        if (!File.Exists(targetFile))
            return;

        throw new RefactoringException(
            ErrorCodes.TargetFileExists,
            $"Destination file already exists: {targetFile}");
    }

    /// <summary>
    /// When default compile items are disabled, add an explicit
    /// <c>Compile Include</c> for <paramref name="targetFile"/>.
    /// SDK-style default glob projects are left unchanged.
    /// </summary>
    internal static string AddExplicitCompileItemIfNeeded(
        string projectXml,
        string projectDirectory,
        string targetFile)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(projectXml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            if (LooksLikeExplicitCompileProject(projectXml))
            {
                throw new RefactoringException(
                    ErrorCodes.DocumentNotEditable,
                    "Could not parse project file to add an explicit Compile item.");
            }

            return projectXml;
        }

        if (!RequiresExplicitCompileItems(document))
            return projectXml;

        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        if (CompileItemRefersToFile(document, ns, projectDirectory, targetFile))
            return projectXml;

        var include = GetCompileIncludePath(projectDirectory, targetFile);
        var compile = new XElement(ns + "Compile", new XAttribute("Include", include));
        var lastCompile = document.Descendants(ns + "Compile").LastOrDefault();
        if (lastCompile != null)
        {
            lastCompile.AddAfterSelf(new XText(Environment.NewLine + "    "));
            lastCompile.AddAfterSelf(compile);
        }
        else if (document.Root != null)
        {
            var itemGroup = new XElement(
                ns + "ItemGroup",
                new XText(Environment.NewLine + "    "),
                compile,
                new XText(Environment.NewLine + "  "));
            document.Root.Add(new XText(Environment.NewLine + "  "));
            document.Root.Add(itemGroup);
            document.Root.Add(new XText(Environment.NewLine));
        }
        else
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                "Project file has no root element; cannot add an explicit Compile item.");
        }

        return SerializeProjectXml(document, projectXml);
    }

    internal static string GetCompileIncludePath(string projectDirectory, string filePath)
    {
        var relative = Path.GetRelativePath(
            PathResolver.NormalizePath(projectDirectory),
            PathResolver.NormalizePath(filePath));
        return relative.Replace('\\', '/');
    }

    private static (string? ProjectPath, string? UpdatedText) PrepareExplicitCompileItemUpdate(
        Project project,
        string targetFile)
    {
        var projectPath = project.FilePath;
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                "Project file is not available to add an explicit Compile item for the new base class file.");
        }

        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(projectDirectory))
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Project '{project.Name}' is not editable.");
        }

        var original = File.ReadAllText(projectPath);
        var updated = AddExplicitCompileItemIfNeeded(original, projectDirectory, targetFile);
        if (string.Equals(original, updated, StringComparison.Ordinal))
            return (projectPath, null);

        if (new FileInfo(projectPath).IsReadOnly)
        {
            throw new RefactoringException(
                ErrorCodes.DocumentNotEditable,
                $"Project '{project.Name}' is not editable.");
        }

        return (projectPath, updated);
    }

    private static void WriteProjectFile(string projectPath, string projectText)
    {
        try
        {
            File.WriteAllText(projectPath, projectText);
        }
        catch (IOException ex)
        {
            throw new RefactoringException(
                ErrorCodes.FilesystemError,
                $"Failed to update project file: {ex.Message}",
                ex);
        }
    }

    private static bool RequiresExplicitCompileItems(XDocument document)
    {
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var compileDefaults = GetMsBuildProperty(document, ns, "EnableDefaultCompileItems");
        var defaultItems = GetMsBuildProperty(document, ns, "EnableDefaultItems");

        if (string.Equals(compileDefaults, "false", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(compileDefaults, "true", StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(defaultItems, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExplicitCompileProject(string projectXml) =>
        ContainsDisabledProperty(projectXml, "EnableDefaultCompileItems")
        || ContainsDisabledProperty(projectXml, "EnableDefaultItems");

    private static bool ContainsDisabledProperty(string projectXml, string propertyName)
    {
        var start = projectXml.IndexOf(propertyName, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;

        var close = projectXml.IndexOf('>', start);
        if (close < 0 || close + 1 >= projectXml.Length)
            return false;

        return projectXml.AsSpan(close + 1).TrimStart().StartsWith("false", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetMsBuildProperty(XDocument document, XNamespace ns, string name) =>
        document.Descendants(ns + name)
            .Select(e => e.Value.Trim())
            .LastOrDefault(v => v.Length > 0);

    private static bool CompileItemRefersToFile(
        XDocument document,
        XNamespace ns,
        string projectDirectory,
        string filePath)
    {
        foreach (var compile in document.Descendants(ns + "Compile"))
        {
            foreach (var attributeName in new[] { "Include", "Update" })
            {
                var value = compile.Attribute(attributeName)?.Value;
                if (!string.IsNullOrWhiteSpace(value)
                    && RenameFileToMatchTypeOperation.ProjectItemRefersToFile(projectDirectory, value, filePath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string SerializeProjectXml(XDocument document, string originalXml)
    {
        var writerSettings = new XmlWriterSettings
        {
            OmitXmlDeclaration = !originalXml.Contains("<?xml", StringComparison.OrdinalIgnoreCase),
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = originalXml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
            Indent = false
        };

        using var writer = new StringWriter();
        using (var xmlWriter = XmlWriter.Create(writer, writerSettings))
        {
            document.Save(xmlWriter);
        }

        var serialized = writer.ToString();
        if (originalXml.EndsWith('\n') && !serialized.EndsWith('\n'))
            serialized += writerSettings.NewLineChars;
        return serialized;
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ExtractBaseClassParams @params,
        List<MemberDeclarationSyntax> members,
        ClassDeclarationSyntax baseClass,
        string? namespaceName,
        string targetFile,
        string? projectPath)
    {
        var memberNames = string.Join(", ", members.SelectMany(GetExtractedMemberNames));
        var baseClassCode = baseClass.NormalizeWhitespace().ToFullString();

        var isNewFile = targetFile != @params.SourceFile;

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = targetFile,
                ChangeType = isNewFile ? ChangeKind.Create : ChangeKind.Modify,
                Description = $"Extract base class {@params.BaseClassName} with members: {memberNames}",
                BeforeSnippet = isNewFile ? "// (new file)" : $"// Before class '{@params.TypeName}'",
                AfterSnippet = baseClassCode
            },
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Update {@params.TypeName} to inherit from {@params.BaseClassName}",
                BeforeSnippet = $"class {@params.TypeName}",
                AfterSnippet = $"class {@params.TypeName} : {@params.BaseClassName}"
            }
        };

        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            pendingChanges.Add(new PendingChange
            {
                File = projectPath,
                ChangeType = ChangeKind.Modify,
                Description = "Add explicit Compile item for the new base class file"
            });
        }

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}
