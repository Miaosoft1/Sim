namespace Miaosoft.Sim.Combat.Skill.Tests;

[TestClass]
public sealed class StrictExpressionTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("1 + 2 * 3", 7d)]
    [DataRow("(1 + 2) * 3", 9d)]
    [DataRow("1e2 / (2 + 3)", 20d)]
    [DataRow("min(4, 1, 3) + MAX(2, 5)", 6d)]
    [DataRow("clamp(15, 2, 10)", 10d)]
    [DataRow("ceil(-1.5) + ceiling(1.2) + floor(1.8) + abs(-2)", 4d)]
    [DataRow("round(2.5) + round(3.5)", 6d)]
    public void NumericOperatorsAndFunctionsUseTheDeclaredSemantics(string text, double expected)
    {
        double? value = CompileNumber(text).EvaluateNumber(new Inputs());
        Assert.AreEqual(expected, value);
        TestContext.WriteLine($"{text} = {value}");
    }

    [TestMethod]
    public void BooleanPrecedenceStringsAndIdentitiesAreTypeChecked()
    {
        Guid owner = Guid.NewGuid();
        Guid source = Guid.NewGuid();
        var inputs = new Inputs
        {
            Value = 0,
            Flag = true,
            Name = "Equipment",
            Owner = owner,
            EventSource = owner,
            ListenerSource = source,
            EventSourceIdentity = source
        };
        var expression = CompileCondition(
            "(event.source == listener.owner && event.origin == listener.origin) && " +
            "stats.value == 0 && stats.flag == true && kind == \"Equipment\" || false");
        Assert.IsTrue(expression.Matches(inputs));
        inputs.Name = "equipment";
        Assert.IsFalse(expression.Matches(inputs));
        inputs.Name = "Equipment";
        inputs.EventSourceIdentity = Guid.NewGuid();
        Assert.IsFalse(expression.Matches(inputs));
        Assert.IsTrue(CompileCondition("\"a\\\"b\" == \"a\\\"b\"").Matches(inputs));
        Assert.IsTrue(CompileCondition("if(event.source == null, true, event.source == listener.owner)").Matches(inputs));
        TestContext.WriteLine("Identity comparisons were role-specific, and strings were compared ordinally.");
    }

    [TestMethod]
    public void NullUsesThreeValuedLogicAndExplicitNullChecks()
    {
        var inputs = new Inputs();
        Assert.IsNull(CompileCondition("event.source == listener.owner").EvaluateCondition(inputs));
        Assert.IsNull(CompileCondition("!(event.source == listener.owner)").EvaluateCondition(inputs));
        Assert.IsTrue(CompileCondition("event.source == null").Matches(inputs));
        Assert.IsFalse(CompileCondition("event.source != null").Matches(inputs));
        Assert.IsTrue(CompileCondition("null == null").Matches(inputs));
        Assert.IsFalse(CompileCondition("kind != \"Equipment\"").Matches(inputs));
        Assert.IsTrue(CompileCondition("kind == null || kind != \"Equipment\"").Matches(inputs));
        Assert.IsFalse(CompileCondition("stats.flag && false").EvaluateCondition(inputs) ?? true);
        Assert.IsTrue(CompileCondition("stats.flag || true").EvaluateCondition(inputs) ?? false);
        Assert.IsNull(CompileCondition("stats.flag && true").EvaluateCondition(inputs));
        Assert.IsNull(CompileCondition("stats.flag || false").EvaluateCondition(inputs));
        TestContext.WriteLine("Missing comparisons stayed unknown; explicit null checks and three-valued logic remained distinct.");
    }

    [TestMethod]
    public void ShortCircuitSkipsRuntimeErrorsButNotStaticTypeChecking()
    {
        var inputs = new Inputs();
        Assert.IsTrue(CompileCondition("true || stats.failure > 0").Matches(inputs));
        Assert.IsFalse(CompileCondition("false && stats.failure > 0").Matches(inputs));
        Assert.ThrowsExactly<ExpressionEvaluationException>(
            () => CompileCondition("stats.flag && stats.failure > 0").EvaluateCondition(inputs));
        Assert.ThrowsExactly<ExpressionEvaluationException>(
            () => CompileCondition("stats.flag || stats.failure > 0").EvaluateCondition(inputs));
        Assert.ThrowsExactly<FormatException>(() => CompileCondition("true || (1 == \"1\")"));
        TestContext.WriteLine("Short-circuited fields were not evaluated; unknown values still evaluated the necessary branch.");
    }

    [TestMethod]
    [DataRow("unknown == 0")]
    [DataRow("1 == \"1\"")]
    [DataRow("true > false")]
    [DataRow("\"a\" < \"b\"")]
    [DataRow("event.source == event.ability")]
    [DataRow("min(1) > 0")]
    [DataRow("min(1, true) > 0")]
    [DataRow("unknownFunction(1) > 0")]
    [DataRow("1e309 > 0")]
    [DataRow("1 % 2 == 1")]
    public void InvalidExpressionsFailAtCompilation(string text)
    {
        Assert.ThrowsExactly<FormatException>(() => CompileCondition(text));
        TestContext.WriteLine($"Rejected: {text}");
    }

    [TestMethod]
    public void EvaluatedOperandsRemainStrictWhenAnotherOperandIsMissing()
    {
        var inputs = new Inputs();
        Assert.ThrowsExactly<ExpressionEvaluationException>(
            () => CompileNumber("stats.value + stats.failure").EvaluateNumber(inputs));
        Assert.ThrowsExactly<ExpressionEvaluationException>(
            () => CompileNumber("min(stats.value, stats.failure)").EvaluateNumber(inputs));
        Assert.IsNull(CompileNumber("stats.value / stats.other").EvaluateNumber(inputs));
        Assert.ThrowsExactly<ExpressionEvaluationException>(
            () => CompileNumber("stats.value / 0").EvaluateNumber(inputs));
        Assert.ThrowsExactly<ExpressionEvaluationException>(
            () => CompileNumber("1 / 0").EvaluateNumber(inputs));
        Assert.ThrowsExactly<ExpressionEvaluationException>(
            () => CompileNumber("1e308 * 1e308").EvaluateNumber(inputs));
        Assert.ThrowsExactly<ExpressionEvaluationException>(
            () => CompileNumber("clamp(1, 3, 2)").EvaluateNumber(inputs));
        Assert.ThrowsExactly<ExpressionEvaluationException>(
            () => CompileNumber("clamp(stats.value, 3, 2)").EvaluateNumber(inputs));
        inputs.Value = double.PositiveInfinity;
        Assert.ThrowsExactly<ExpressionEvaluationException>(
            () => CompileNumber("stats.value").EvaluateNumber(inputs));
        TestContext.WriteLine("Missing propagated only after required operands ran; numeric domain errors were explicit.");
    }

    [TestMethod]
    public void ConditionalExpressionsAreTypedLazyAndPreserveUnknownConditions()
    {
        var inputs = new Inputs { Flag = true, Value = 4 };
        Assert.AreEqual(10d, CompileNumber("if(stats.flag, 10, 1 / 0)").EvaluateNumber(inputs));
        inputs.Flag = false;
        Assert.AreEqual(2d, CompileNumber("if(stats.flag, 1 / 0, stats.value / 2)").EvaluateNumber(inputs));
        inputs.Flag = null;
        Assert.IsNull(CompileNumber("if(stats.flag, 10, 20)").EvaluateNumber(inputs));
        Assert.ThrowsExactly<FormatException>(() => CompileNumber("if(stats.value, 1, 2)"));
        Assert.ThrowsExactly<FormatException>(() => CompileNumber("if(true, 1, \"two\")"));
        TestContext.WriteLine("Conditional formulas evaluated only the selected branch and kept nullable predicates unknown.");
    }

    [TestMethod]
    public void NumericFallbackIsProductionOnlyReportedAndMarked()
    {
        ExpressionSchema<Inputs> schema = CreateSchema();
        var formula = NumericFormula<Inputs>.Compile("10 / stats.value", schema, "max(1, stats.other)");
        var inputs = new Inputs { Value = 0, Other = 2 };
        var diagnostics = new List<FormulaDiagnostic>();

        Assert.ThrowsExactly<ExpressionEvaluationException>(
            () => formula.Evaluate(inputs, FormulaEvaluationMode.Strict, diagnostics.Add));
        FormulaEvaluationResult recovered = formula.Evaluate(inputs, FormulaEvaluationMode.Production, diagnostics.Add);
        Assert.AreEqual(2d, recovered.Value);
        Assert.IsTrue(recovered.UsedFallback);
        Assert.HasCount(1, diagnostics);
        StringAssert.Contains(diagnostics[0].Error.Message, "Division by zero");
        Assert.ThrowsExactly<InvalidOperationException>(
            () => formula.Evaluate(inputs, FormulaEvaluationMode.Production));

        inputs.Value = 5;
        FormulaEvaluationResult normal = formula.Evaluate(inputs, FormulaEvaluationMode.Production, diagnostics.Add);
        Assert.AreEqual(2d, normal.Value);
        Assert.IsFalse(normal.UsedFallback);
        Assert.HasCount(1, diagnostics);
        TestContext.WriteLine("Production fallback reported the original error and marked the result; strict mode failed.");
    }

    [TestMethod]
    public void MissingOrFailedFallbackAndUnexpectedErrorsRemainFailures()
    {
        ExpressionSchema<Inputs> schema = CreateSchema();
        var inputs = new Inputs();
        var diagnostics = new List<FormulaDiagnostic>();
        var missing = NumericFormula<Inputs>.Compile("stats.value", schema, "3");
        Assert.AreEqual(3d, missing.Evaluate(inputs, FormulaEvaluationMode.Production, diagnostics.Add).Value);
        var absent = NumericFormula<Inputs>.Compile("stats.value", schema);
        Assert.ThrowsExactly<ExpressionEvaluationException>(
            () => absent.Evaluate(inputs, FormulaEvaluationMode.Production, diagnostics.Add));
        var failed = NumericFormula<Inputs>.Compile("1 / 0", schema, "1 / 0");
        Assert.ThrowsExactly<ExpressionEvaluationException>(
            () => failed.Evaluate(inputs, FormulaEvaluationMode.Production, diagnostics.Add));
        var bug = NumericFormula<Inputs>.Compile("stats.bug", schema, "3");
        Assert.ThrowsExactly<InvalidOperationException>(
            () => bug.Evaluate(inputs, FormulaEvaluationMode.Production, diagnostics.Add));
        Assert.ThrowsExactly<FormatException>(() => NumericFormula<Inputs>.Compile("1", schema, "stats.unknown"));
        TestContext.WriteLine("A missing primary could recover explicitly; missing/failed fallback and unexpected errors did not.");
    }

    [TestMethod]
    public void SealedSchemasBindIdenticalTextIndependentlyAndRejectChanges()
    {
        var first = new ExpressionSchema<Inputs>();
        var second = new ExpressionSchema<Inputs>();
        first.RegisterNumber("value", _ => 1d);
        second.RegisterNumber("value", _ => 2d);
        Assert.ThrowsExactly<InvalidOperationException>(() => first.RegisterNumber("value", _ => 3d));
        Assert.ThrowsExactly<ArgumentException>(() => first.RegisterNumber("not..valid", _ => 0d));
        Assert.ThrowsExactly<ArgumentException>(() => first.RegisterBoolean("true", _ => true));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => StrictExpressionCompiler.Compile("value", first, ExpressionValueType.Number));
        first.Seal();
        second.Seal();
        Assert.AreEqual(1d, StrictExpressionCompiler.Compile("value", first, ExpressionValueType.Number)
            .EvaluateNumber(new Inputs()));
        Assert.AreEqual(2d, StrictExpressionCompiler.Compile("value", second, ExpressionValueType.Number)
            .EvaluateNumber(new Inputs()));
        Assert.ThrowsExactly<InvalidOperationException>(() => first.RegisterNumber("other", _ => 0d));
        Assert.AreEqual(new ExpressionFieldDescriptor("value", ExpressionValueType.Number, false), first.Fields.Single());
        TestContext.WriteLine("The same expression text used each sealed schema's own field binding.");
    }

    private static CompiledExpression<Inputs> CompileNumber(string text) =>
        StrictExpressionCompiler.Compile(text, CreateSchema(), ExpressionValueType.Number);

    private static CompiledExpression<Inputs> CompileCondition(string text) =>
        StrictExpressionCompiler.Compile(text, CreateSchema(), ExpressionValueType.Boolean);

    private static ExpressionSchema<Inputs> CreateSchema()
    {
        var schema = new ExpressionSchema<Inputs>();
        schema.RegisterNumber("stats.value", input => input.Value, nullable: true);
        schema.RegisterNumber("stats.other", input => input.Other, nullable: true);
        schema.RegisterNumber("stats.failure", _ => throw new ExpressionEvaluationException("Field failure."));
        schema.RegisterNumber("stats.bug", _ => throw new InvalidOperationException("Unexpected module bug."));
        schema.RegisterBoolean("stats.flag", input => input.Flag, nullable: true);
        schema.RegisterString("kind", input => input.Name, nullable: true);
        schema.RegisterIdentity("listener.owner", ExpressionValueType.UnitIdentity, input => input.Owner, nullable: true);
        schema.RegisterIdentity("event.source", ExpressionValueType.UnitIdentity, input => input.EventSource, nullable: true);
        schema.RegisterIdentity("event.ability", ExpressionValueType.AbilityIdentity, input => input.Ability, nullable: true);
        schema.RegisterIdentity("listener.origin", ExpressionValueType.SourceIdentity, input => input.ListenerSource, nullable: true);
        schema.RegisterIdentity("event.origin", ExpressionValueType.SourceIdentity, input => input.EventSourceIdentity, nullable: true);
        schema.Seal();
        return schema;
    }

    private sealed class Inputs
    {
        public double? Value { get; set; }
        public double? Other { get; set; }
        public bool? Flag { get; set; }
        public string? Name { get; set; }
        public Guid? Owner { get; set; }
        public Guid? EventSource { get; set; }
        public Guid? Ability { get; set; }
        public Guid? ListenerSource { get; set; }
        public Guid? EventSourceIdentity { get; set; }
    }
}
