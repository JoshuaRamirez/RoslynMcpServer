# Roslyn MCP Server — Feature Catalog (`v1.0.0`)

Authoritative catalog of every user-facing capability in `RoslynMcpServer` (`roslyn-mcp` and `roslyn-cli`).

## 1. 5-Layer Action Algebra (`v1.0.0`)

| Layer | Feature | Contract / Engine Types | MCP / CLI Surface |Since |
|---|---|---|---|---|
| **Layer 1** | Universal `TargetCoordinate` (`DocumentScope × Anchor × EnclosingNormalization`) | `TargetCoordinate`, `DocumentScope`, `Anchor`, `EnclosingNormalization`, `TargetCoordinateResolver` | `$defs/TargetCoordinate` | `1.0.0` |
| **Layer 2** | Composable `SymbolPredicate` AST & Single-Pass Evaluator | `SymbolPredicate`, `IntRange`, `QuerySymbolsRequest`, `SymbolQueryResult`, `SymbolPredicateEvaluator` | `roslyn_query_symbols` / `query-symbols` | `1.0.0` |
| **Layer 3** | Warm `SolutionSessionManager` & Atomic `SolutionTransaction` Pipeline | `SolutionSessionManager`, `WarmSolutionSession`, `SolutionTransactionRequest`, `SolutionTransactionResult`, `SolutionTransactionEngine` | `roslyn_transaction` / `transaction` | `1.0.0` |
| **Layer 4** | `CodeActionHost`, `SyntaxGenerator` / `SymbolEditor` & `IOperationSemanticRewriter` | `CodeActionHostRunner`, `SyntaxGeneratorRunner`, `IOperationSemanticRewriter`, `SemanticPatternKind` | `SolutionOperation` (`code_action_host`, `syntax_generator`, `semantic_pattern_rewrite`) | `1.0.0` |
| **Layer 5** | Compact `$defs` JSON Schema & 68-Tool Registry | `CompactSchemaDefs`, `RoslynTransactionTool`, `RoslynQuerySymbolsTool` | `roslyn_transaction`, `roslyn_query_symbols`, `roslyn-cli` | `1.0.0` |

## 2. Refactoring Operations (`54` Tools)

- **Extract & Hierarchy (`13`)**: `extract_method`, `extract_variable`, `extract_constant`, `extract_interface`, `extract_base_class`, `introduce_parameter`, `introduce_field`, `pull_members_up`, `push_members_down`, `use_base_type`, `safe_delete`, `make_static`, `make_non_static`
- **Rename & Move (`5`)**: `rename_symbol`, `rename_file_to_match_type`, `rename_namespace`, `move_type_to_file`, `move_type_to_namespace`
- **Inline (`3`)**: `inline_variable`, `inline_method`, `inline_constant`
- **Signature & Encapsulation (`6`)**: `change_signature`, `add_parameter`, `remove_parameter`, `reorder_parameters`, `change_return_type`, `encapsulate_field`
- **Code Conversion (`13`)**: `convert_to_async`, `convert_expression_body`, `convert_to_block_body`, `invert_if`, `add_braces`, `remove_braces`, `simplify_name`, `convert_property`, `convert_foreach_linq`, `convert_anonymous_to_class`, `convert_tuple_to_struct`, `convert_to_interpolated_string`, `convert_to_pattern_matching`
- **Code Generation (`9`)**: `generate_constructor`, `generate_property`, `generate_method_stub`, `generate_equals_hashcode`, `generate_overrides`, `generate_tostring`, `implement_interface`, `implement_abstract`, `add_null_checks`
- **Organize & Format (`4`)**: `add_missing_usings`, `remove_unused_usings`, `sort_usings`, `format_document`
- **Transactional Pipeline (`1`)**: `roslyn_transaction` (`transaction`)

## 3. Query & Analysis Operations (`13` Tools)

- **Navigation & Search (`6`)**: `find_references`, `find_callers`, `find_implementations`, `go_to_definition`, `search_symbols`, `roslyn_query_symbols` (`query-symbols`)
- **Analysis, Metrics & Hierarchy (`7`)**: `get_diagnostics`, `get_code_metrics`, `analyze_control_flow`, `analyze_data_flow`, `get_document_outline`, `get_symbol_info`, `get_type_hierarchy`

## 4. Environment & Diagnostics (`1` Tool)

- **Health Check (`1`)**: `diagnose`
