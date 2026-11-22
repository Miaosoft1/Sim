using System.Globalization;
using System.Text;

namespace Miaosoft.Sim.Combat.Skill.Expression;

public static class StrictExpressionCompiler
{
    public static CompiledExpression<TContext> Compile<TContext>(
        string expression, ExpressionSchema<TContext> schema, ExpressionValueType expectedType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(schema);
        if (!schema.IsSealed) throw new InvalidOperationException("The expression schema must be sealed before compilation.");
        if (expectedType is not (ExpressionValueType.Number or ExpressionValueType.Boolean))
            throw new ArgumentOutOfRangeException(nameof(expectedType));

        BoundExpressionNode<TContext> root = new Parser<TContext>(expression, schema).Parse();
        if (root.Type != expectedType)
            throw new FormatException($"Expression must return {expectedType}, not {root.Type?.ToString() ?? "null"}.");
        return new CompiledExpression<TContext>(root);
    }

    private enum TokenKind
    {
        End, Number, String, Identifier, Open, Close, Comma,
        Plus, Minus, Multiply, Divide, Not,
        Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual, And, Or
    }

    private readonly record struct Token(TokenKind Kind, string Text, double Number = 0);

    private sealed class Lexer(string text)
    {
        private int position;

        public Token Next()
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            if (position == text.Length) return new Token(TokenKind.End, "");
            char current = text[position];
            if (IsDigit(current) || current == '.' && position + 1 < text.Length && IsDigit(text[position + 1]))
                return ReadNumber();
            if (char.IsLetter(current) || current == '_') return ReadIdentifier();
            if (current == '"') return ReadString();

            position++;
            return current switch
            {
                '(' => new(TokenKind.Open, "("),
                ')' => new(TokenKind.Close, ")"),
                ',' => new(TokenKind.Comma, ","),
                '+' => new(TokenKind.Plus, "+"),
                '-' => new(TokenKind.Minus, "-"),
                '*' => new(TokenKind.Multiply, "*"),
                '/' => new(TokenKind.Divide, "/"),
                '!' => Take('=') ? new(TokenKind.NotEqual, "!=") : new(TokenKind.Not, "!"),
                '=' when Take('=') => new(TokenKind.Equal, "=="),
                '<' => Take('=') ? new(TokenKind.LessEqual, "<=") : new(TokenKind.Less, "<"),
                '>' => Take('=') ? new(TokenKind.GreaterEqual, ">=") : new(TokenKind.Greater, ">"),
                '&' when Take('&') => new(TokenKind.And, "&&"),
                '|' when Take('|') => new(TokenKind.Or, "||"),
                _ => throw new FormatException($"Unexpected character '{current}' at position {position - 1}.")
            };
        }

        private Token ReadNumber()
        {
            int start = position;
            while (position < text.Length && IsDigit(text[position])) position++;
            if (position < text.Length && text[position] == '.')
            {
                position++;
                while (position < text.Length && IsDigit(text[position])) position++;
            }
            if (position < text.Length && text[position] is 'e' or 'E')
            {
                position++;
                if (position < text.Length && text[position] is '+' or '-') position++;
                int exponent = position;
                while (position < text.Length && IsDigit(text[position])) position++;
                if (exponent == position) throw new FormatException("A numeric exponent requires digits.");
            }
            string value = text[start..position];
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
                !double.IsFinite(number))
                throw new FormatException($"Invalid finite number literal '{value}'.");
            return new Token(TokenKind.Number, value, number);
        }

        private Token ReadIdentifier()
        {
            int start = position++;
            while (position < text.Length &&
                (char.IsLetterOrDigit(text[position]) || text[position] is '_' or '.'))
                position++;
            return new Token(TokenKind.Identifier, text[start..position]);
        }

        private Token ReadString()
        {
            position++;
            var value = new StringBuilder();
            while (position < text.Length)
            {
                char character = text[position++];
                if (character == '"') return new Token(TokenKind.String, value.ToString());
                if (character is '\r' or '\n') throw new FormatException("A string literal cannot contain a raw newline.");
                if (character != '\\')
                {
                    value.Append(character);
                    continue;
                }
                if (position == text.Length) throw new FormatException("Incomplete string escape.");
                char escaped = text[position++];
                value.Append(escaped switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => throw new FormatException($"Unsupported string escape '\\{escaped}'.")
                });
            }
            throw new FormatException("Unterminated string literal.");
        }

        private bool Take(char expected)
        {
            if (position >= text.Length || text[position] != expected) return false;
            position++;
            return true;
        }

        private static bool IsDigit(char character) => character is >= '0' and <= '9';
    }

    private sealed class Parser<TContext>
    {
        private readonly Lexer lexer;
        private readonly ExpressionSchema<TContext> schema;
        private Token current;

        public Parser(string text, ExpressionSchema<TContext> schema)
        {
            lexer = new Lexer(text);
            this.schema = schema;
            current = lexer.Next();
        }

        public BoundExpressionNode<TContext> Parse()
        {
            BoundExpressionNode<TContext> result = ParseOr();
            if (current.Kind != TokenKind.End)
                throw new FormatException($"Unexpected token '{current.Text}' at the end of the expression.");
            return result;
        }

        private BoundExpressionNode<TContext> ParseOr()
        {
            BoundExpressionNode<TContext> left = ParseAnd();
            while (current.Kind == TokenKind.Or)
            {
                TokenKind operation = Consume().Kind;
                left = Binary(operation, left, ParseAnd());
            }
            return left;
        }

        private BoundExpressionNode<TContext> ParseAnd()
        {
            BoundExpressionNode<TContext> left = ParseEquality();
            while (current.Kind == TokenKind.And)
            {
                TokenKind operation = Consume().Kind;
                left = Binary(operation, left, ParseEquality());
            }
            return left;
        }

        private BoundExpressionNode<TContext> ParseEquality()
        {
            BoundExpressionNode<TContext> left = ParseRelational();
            while (current.Kind is TokenKind.Equal or TokenKind.NotEqual)
            {
                TokenKind operation = Consume().Kind;
                left = Binary(operation, left, ParseRelational());
            }
            return left;
        }

        private BoundExpressionNode<TContext> ParseRelational()
        {
            BoundExpressionNode<TContext> left = ParseAdditive();
            while (current.Kind is TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual)
            {
                TokenKind operation = Consume().Kind;
                left = Binary(operation, left, ParseAdditive());
            }
            return left;
        }

        private BoundExpressionNode<TContext> ParseAdditive()
        {
            BoundExpressionNode<TContext> left = ParseMultiplicative();
            while (current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                TokenKind operation = Consume().Kind;
                left = Binary(operation, left, ParseMultiplicative());
            }
            return left;
        }

        private BoundExpressionNode<TContext> ParseMultiplicative()
        {
            BoundExpressionNode<TContext> left = ParseUnary();
            while (current.Kind is TokenKind.Multiply or TokenKind.Divide)
            {
                TokenKind operation = Consume().Kind;
                left = Binary(operation, left, ParseUnary());
            }
            return left;
        }

        private BoundExpressionNode<TContext> ParseUnary()
        {
            if (current.Kind is not (TokenKind.Not or TokenKind.Plus or TokenKind.Minus))
                return ParsePrimary();
            TokenKind operation = Consume().Kind;
            BoundExpressionNode<TContext> operand = ParseUnary();
            ExpressionValueType type = operation == TokenKind.Not
                ? ExpressionValueType.Boolean : ExpressionValueType.Number;
            RequireType(operand, type, "Unary operator");
            return new UnaryNode<TContext>(operation, operand, type);
        }

        private BoundExpressionNode<TContext> ParsePrimary()
        {
            if (current.Kind == TokenKind.Number)
                return new LiteralNode<TContext>(ExpressionValue.Number(Consume().Number));
            if (current.Kind == TokenKind.String)
                return new LiteralNode<TContext>(ExpressionValue.String(Consume().Text));
            if (current.Kind == TokenKind.Open)
            {
                Consume();
                BoundExpressionNode<TContext> inner = ParseOr();
                Expect(TokenKind.Close);
                return inner;
            }
            if (current.Kind != TokenKind.Identifier)
                throw new FormatException($"Expected an expression, not '{current.Text}'.");

            string name = Consume().Text;
            if (name == "true") return new LiteralNode<TContext>(ExpressionValue.Boolean(true));
            if (name == "false") return new LiteralNode<TContext>(ExpressionValue.Boolean(false));
            if (name == "null") return new NullNode<TContext>();
            if (current.Kind == TokenKind.Open) return ParseFunction(name);
            return new FieldNode<TContext>(schema.GetField(name));
        }

        private BoundExpressionNode<TContext> ParseFunction(string name)
        {
            Expect(TokenKind.Open);
            var arguments = new List<BoundExpressionNode<TContext>>();
            if (current.Kind != TokenKind.Close)
            {
                do
                {
                    arguments.Add(ParseOr());
                    if (current.Kind != TokenKind.Comma) break;
                    Consume();
                } while (true);
            }
            Expect(TokenKind.Close);
            string function = name.ToLowerInvariant();
            bool validCount = function switch
            {
                "min" or "max" => arguments.Count >= 2,
                "clamp" => arguments.Count == 3,
                "if" => arguments.Count == 3,
                "floor" or "ceil" or "ceiling" or "round" or "abs" => arguments.Count == 1,
                _ => throw new FormatException($"Unknown expression function '{name}'.")
            };
            if (!validCount) throw new FormatException($"Invalid argument count for function '{name}'.");
            if (function == "if")
            {
                if (arguments.Count != 3)
                    throw new FormatException("Function 'if' requires a condition and two branches.");
                RequireType(arguments[0], ExpressionValueType.Boolean, "Function 'if' condition");
                if (arguments[1].Type is not { } branchType || branchType != arguments[2].Type)
                    throw new FormatException("Function 'if' branches must have the same non-null type.");
                return new ConditionalNode<TContext>(arguments[0], arguments[1], arguments[2], branchType);
            }
            foreach (BoundExpressionNode<TContext> argument in arguments)
                RequireType(argument, ExpressionValueType.Number, $"Function '{name}'");
            return new FunctionNode<TContext>(function, arguments.ToArray());
        }

        private sealed class ConditionalNode<TConditionalContext>(
            BoundExpressionNode<TConditionalContext> condition,
            BoundExpressionNode<TConditionalContext> whenTrue,
            BoundExpressionNode<TConditionalContext> whenFalse,
            ExpressionValueType type) : BoundExpressionNode<TConditionalContext>(type)
        {
            public override ExpressionValue Evaluate(TConditionalContext context)
            {
                ExpressionValue predicate = condition.Evaluate(context);
                if (!predicate.HasValue) return ExpressionValue.Missing(type);
                return (predicate.AsBoolean ? whenTrue : whenFalse).Evaluate(context);
            }
        }

        private static BoundExpressionNode<TContext> Binary(
            TokenKind operation, BoundExpressionNode<TContext> left, BoundExpressionNode<TContext> right)
        {
            if (operation is TokenKind.And or TokenKind.Or)
            {
                RequireType(left, ExpressionValueType.Boolean, "Logical operator");
                RequireType(right, ExpressionValueType.Boolean, "Logical operator");
                return new BinaryNode<TContext>(operation, left, right, ExpressionValueType.Boolean);
            }
            if (operation is TokenKind.Equal or TokenKind.NotEqual)
            {
                if (left.Type != null && right.Type != null && left.Type != right.Type)
                    throw new FormatException("Equality operands must have the same type.");
                return new BinaryNode<TContext>(operation, left, right, ExpressionValueType.Boolean);
            }

            RequireType(left, ExpressionValueType.Number, "Numeric operator");
            RequireType(right, ExpressionValueType.Number, "Numeric operator");
            ExpressionValueType result = operation is TokenKind.Less or TokenKind.LessEqual
                or TokenKind.Greater or TokenKind.GreaterEqual
                ? ExpressionValueType.Boolean : ExpressionValueType.Number;
            return new BinaryNode<TContext>(operation, left, right, result);
        }

        private static void RequireType(
            BoundExpressionNode<TContext> node, ExpressionValueType type, string description)
        {
            if (node.Type != type)
                throw new FormatException($"{description} requires {type}, not {node.Type?.ToString() ?? "null"}.");
        }

        private Token Consume()
        {
            Token result = current;
            current = lexer.Next();
            return result;
        }

        private void Expect(TokenKind kind)
        {
            if (current.Kind != kind)
                throw new FormatException($"Expected {kind}, not '{current.Text}'.");
            Consume();
        }
    }

    private sealed class LiteralNode<TContext>(ExpressionValue value) : BoundExpressionNode<TContext>(value.Type)
    {
        public override ExpressionValue Evaluate(TContext context) => value;
    }

    private sealed class NullNode<TContext>() : BoundExpressionNode<TContext>(null)
    {
        public override ExpressionValue Evaluate(TContext context) => ExpressionValue.Missing(ExpressionValueType.Boolean);
    }

    private sealed class FieldNode<TContext>(BoundField<TContext> field)
        : BoundExpressionNode<TContext>(field.Descriptor.Type)
    {
        public override ExpressionValue Evaluate(TContext context) => field.Read(context);
    }

    private sealed class UnaryNode<TContext>(
        TokenKind operation, BoundExpressionNode<TContext> operand, ExpressionValueType type)
        : BoundExpressionNode<TContext>(type)
    {
        public override ExpressionValue Evaluate(TContext context)
        {
            ExpressionValue value = operand.Evaluate(context);
            if (!value.HasValue) return ExpressionValue.Missing(Type!.Value);
            return operation switch
            {
                TokenKind.Not => ExpressionValue.Boolean(!value.AsBoolean),
                TokenKind.Plus => value,
                TokenKind.Minus => Finite(-value.AsNumber),
                _ => throw new InvalidOperationException("Unsupported unary operator.")
            };
        }
    }

    private sealed class BinaryNode<TContext>(
        TokenKind operation, BoundExpressionNode<TContext> left, BoundExpressionNode<TContext> right,
        ExpressionValueType type) : BoundExpressionNode<TContext>(type)
    {
        public override ExpressionValue Evaluate(TContext context)
        {
            ExpressionValue first = left.Evaluate(context);
            if (operation == TokenKind.And && first.HasValue && !first.AsBoolean)
                return ExpressionValue.Boolean(false);
            if (operation == TokenKind.Or && first.HasValue && first.AsBoolean)
                return ExpressionValue.Boolean(true);
            ExpressionValue second = right.Evaluate(context);

            if (operation is TokenKind.And or TokenKind.Or)
                return Logical(first, second, operation);
            if (operation is (TokenKind.Equal or TokenKind.NotEqual) && (left.Type == null || right.Type == null))
            {
                bool bothMissing = !first.HasValue && !second.HasValue;
                return ExpressionValue.Boolean(operation == TokenKind.Equal ? bothMissing : !bothMissing);
            }
            if (operation == TokenKind.Divide && second.HasValue && second.AsNumber == 0)
                throw new ExpressionEvaluationException("Division by zero.");
            if (!first.HasValue || !second.HasValue) return ExpressionValue.Missing(Type!.Value);
            if (operation is TokenKind.Equal or TokenKind.NotEqual)
            {
                bool equal = first.Type switch
                {
                    ExpressionValueType.Number => first.AsNumber == second.AsNumber,
                    ExpressionValueType.Boolean => first.AsBoolean == second.AsBoolean,
                    ExpressionValueType.String => string.Equals(first.AsString, second.AsString, StringComparison.Ordinal),
                    ExpressionValueType.UnitIdentity or ExpressionValueType.AbilityIdentity
                        or ExpressionValueType.SourceIdentity => first.AsIdentity == second.AsIdentity,
                    _ => throw new InvalidOperationException("Unsupported equality type.")
                };
                return ExpressionValue.Boolean(operation == TokenKind.Equal ? equal : !equal);
            }

            double a = first.AsNumber;
            double b = second.AsNumber;
            return operation switch
            {
                TokenKind.Plus => Finite(a + b),
                TokenKind.Minus => Finite(a - b),
                TokenKind.Multiply => Finite(a * b),
                TokenKind.Divide => Finite(a / b),
                TokenKind.Less => ExpressionValue.Boolean(a < b),
                TokenKind.LessEqual => ExpressionValue.Boolean(a <= b),
                TokenKind.Greater => ExpressionValue.Boolean(a > b),
                TokenKind.GreaterEqual => ExpressionValue.Boolean(a >= b),
                _ => throw new InvalidOperationException("Unsupported binary operator.")
            };
        }

        private static ExpressionValue Logical(ExpressionValue first, ExpressionValue second, TokenKind operation)
        {
            if (operation == TokenKind.And && second.HasValue && !second.AsBoolean)
                return ExpressionValue.Boolean(false);
            if (operation == TokenKind.Or && second.HasValue && second.AsBoolean)
                return ExpressionValue.Boolean(true);
            if (!first.HasValue || !second.HasValue) return ExpressionValue.Missing(ExpressionValueType.Boolean);
            return ExpressionValue.Boolean(operation == TokenKind.And
                ? first.AsBoolean && second.AsBoolean : first.AsBoolean || second.AsBoolean);
        }
    }

    private sealed class FunctionNode<TContext>(string function, BoundExpressionNode<TContext>[] arguments)
        : BoundExpressionNode<TContext>(ExpressionValueType.Number)
    {
        public override ExpressionValue Evaluate(TContext context)
        {
            var numbers = new double[arguments.Length];
            var present = new bool[arguments.Length];
            bool missing = false;
            for (int index = 0; index < arguments.Length; index++)
            {
                ExpressionValue value = arguments[index].Evaluate(context);
                if (!value.HasValue) missing = true;
                else
                {
                    numbers[index] = value.AsNumber;
                    present[index] = true;
                }
            }
            if (function == "clamp" && present[1] && present[2] && numbers[1] > numbers[2])
                throw new ExpressionEvaluationException("The clamp minimum exceeds its maximum.");
            if (missing) return ExpressionValue.Missing(ExpressionValueType.Number);
            double result = function switch
            {
                "min" => numbers.Min(),
                "max" => numbers.Max(),
                "clamp" => Math.Clamp(numbers[0], numbers[1], numbers[2]),
                "floor" => Math.Floor(numbers[0]),
                "ceil" or "ceiling" => Math.Ceiling(numbers[0]),
                "round" => Math.Round(numbers[0], MidpointRounding.ToEven),
                "abs" => Math.Abs(numbers[0]),
                _ => throw new InvalidOperationException("Unsupported numeric function.")
            };
            return Finite(result);
        }
    }

    private static ExpressionValue Finite(double value) => double.IsFinite(value)
        ? ExpressionValue.Number(value)
        : throw new ExpressionEvaluationException("A numeric expression produced a non-finite result.");
}
