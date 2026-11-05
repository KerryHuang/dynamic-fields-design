namespace DynamicModules.Domain.Formulas;

public enum FormulaValueType { Number, Date, Boolean }

/// <summary>公式 AST。比較只會出現在 <see cref="IfNode"/> 的條件。</summary>
public abstract record FormulaNode;

public sealed record NumberNode(decimal Value) : FormulaNode;
public sealed record FieldRefNode(long FieldId) : FormulaNode;
public sealed record NegateNode(FormulaNode Operand) : FormulaNode;
public sealed record BinaryNode(char Op, FormulaNode Left, FormulaNode Right) : FormulaNode;
public sealed record CompareNode(string Op, FormulaNode Left, FormulaNode Right) : FormulaNode;
public sealed record RoundNode(FormulaNode Value, int Digits) : FormulaNode;
public sealed record IfNode(FormulaNode Condition, FormulaNode Then, FormulaNode Else) : FormulaNode;
public sealed record DateDiffNode(FormulaNode Left, FormulaNode Right) : FormulaNode;
public sealed record DateAddNode(FormulaNode Date, FormulaNode Days) : FormulaNode;
public sealed record TodayNode : FormulaNode;
