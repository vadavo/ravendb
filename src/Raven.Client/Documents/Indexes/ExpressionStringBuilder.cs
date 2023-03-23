// //-----------------------------------------------------------------------
// // <copyright company="Hibernating Rhinos LTD">
// //     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// // </copyright>
// //-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Raven.Client.Documents.Conventions;
using Raven.Client.Extensions;
using Raven.Client.Util;

namespace Raven.Client.Documents.Indexes
{
    /// <summary>
    ///   Based off of System.Linq.Expressions.ExpressionStringBuilder
    /// </summary>
    internal class ExpressionStringBuilder : ExpressionVisitor
    {
        // Fields
        private readonly StringBuilder _out = new StringBuilder();
        private readonly DocumentConventions _conventions;
        private readonly Type _queryRoot;
        private readonly string _queryRootName;
        private readonly bool _translateIdentityProperty;
        private ExpressionOperatorPrecedence _currentPrecedence;
        private Dictionary<object, int> _ids;
        private readonly Dictionary<string, object> _duplicatedParams = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private bool _castLambdas;
        private bool _isDictionary;
        private bool _isReduce;

        // Methods
        private ExpressionStringBuilder(DocumentConventions conventions, bool translateIdentityProperty, Type queryRoot,
            string queryRootName, bool isReduce)
        {
            _conventions = conventions;
            _translateIdentityProperty = translateIdentityProperty;
            _queryRoot = queryRoot;
            _queryRootName = queryRootName;
            _isReduce = isReduce;
        }

        private int GetLabelId(LabelTarget label)
        {
            if (_ids == null)
            {
                _ids = new Dictionary<object, int> { { label, 0 } };
            }
            else if (!_ids.ContainsKey(label))
            {
                _ids.Add(label, _ids.Count);
            }
            return _ids.Count;
        }

        private void AddParam(ParameterExpression p)
        {
            if (_ids == null)
            {
                _ids = new Dictionary<object, int>();
                _ids.Add(_ids, 0);
            }
            else if (!_ids.ContainsKey(p))
            {
                _ids.Add(p, _ids.Count);
            }
        }

        private void DumpLabel(LabelTarget target)
        {
            if (!string.IsNullOrEmpty(target.Name))
            {
                Out(target.Name);
            }
            else
            {
                Out("UnnamedLabel_" + GetLabelId(target));
            }
        }

        /// <summary>
        ///   Convert the expression to a string
        /// </summary>
        public static string ExpressionToString(DocumentConventions conventions, bool translateIdentityProperty, Type queryRoot,
            string queryRootName, Expression node, bool isReduce)
        {
            var builder = new ExpressionStringBuilder(conventions, translateIdentityProperty, queryRoot, queryRootName, isReduce);
            builder.Visit(node, ExpressionOperatorPrecedence.ParenthesisNotNeeded);
            return builder.ToString();
        }

        private int GetParamId(ParameterExpression p)
        {
            int count;
            if (_ids == null)
            {
                _ids = new Dictionary<object, int>();
                AddParam(p);
                return 0;
            }
            if (!_ids.TryGetValue(p, out count))
            {
                count = _ids.Count;
                AddParam(p);
            }
            return count;
        }

        private void Out(char c)
        {
            _out.Append(c);
        }

        private void Out(string s)
        {
            _out.Append(s);
        }

        private void OutMember(Expression instance, MemberInfo member, Type exprType)
        {
            var isId = false;

            string name = null;
            if (_conventions.PropertyNameConverter != null)
            {
                //do not use convention for types in system namespaces
                if (member.DeclaringType?.Namespace?.StartsWith("System") == false &&
                   member.DeclaringType?.Namespace?.StartsWith("Microsoft") == false)
                    name = _conventions.PropertyNameConverter(member);
            }

            if (string.IsNullOrWhiteSpace(name))
                name = GetPropertyName(member.Name, exprType);

            if (TranslateToDocumentId(instance, member, exprType))
            {
                isId = true;
                Out("Id(");
            }

            if (instance != null)
            {
                if (ShouldParenthesisMemberExpression(instance))
                    Out("(");
                Visit(instance);
                if (ShouldParenthesisMemberExpression(instance))
                    Out(")");

                if (isId == false)
                    OutMemberCall(name);
            }
            else
            {
                var parentType = member.DeclaringType;
                while (parentType.IsNested)
                {
                    parentType = parentType.DeclaringType;
                    if (parentType == null)
                        break;
                    Out(parentType.Name + ".");
                }

                Out(member.DeclaringType.Name);

                if (isId == false)
                    OutMemberCall(name);
            }

            if (isId)
                Out(")");
        }

        private void OutMemberCall(string name)
        {
            if (ValidCSharpName(name))
            {
                Out("." + name);
            }
            else
            {
                Out("[\"");
                OutLiteral(name);
                Out("\"]");
            }
        }

        private static bool ValidCSharpName(string name)
        {
            if (name == null)
                return false;

            if (name.Length > 512 || name.Length <= 0)
                return false;

            if (Regex.IsMatch(name, @"<>([a-z])__TransparentIdentifier(\w+)"))
                return true; // we are transforming those to 'thisX' where X is extracted from the name

            if (char.IsLetter(name[0]) == false && name[0] != '_')
                return false;

            for (int i = 1; i < name.Length; i++)
            {
                if (!char.IsLetterOrDigit(name[i]) && name[i] != '_')
                    return false;
            }
            return true;
        }

        private static bool ShouldParenthesisMemberExpression(Expression instance)
        {
            switch (instance.NodeType)
            {
                case ExpressionType.Parameter:
                case ExpressionType.MemberAccess:
                    return false;
                default:
                    return true;
            }
        }

        private bool TranslateToDocumentId(Expression instance, MemberInfo member, Type exprType)
        {
            if (_translateIdentityProperty == false)
                return false;

            if (_conventions.GetIdentityProperty(member.DeclaringType) != member)
                return false;

            // types used in LoadDocument should be translated
            if (_loadDocumentTypes.Contains(exprType))
                return true;

            // only translate from the root type or derivatives
            if (_queryRoot != null && (exprType.IsAssignableFrom(_queryRoot) == false))
                return false;

            if (_queryRootName == null)
                return true; // just in case, shouldn't really happen

            // only translate from the root alias
            string memberName = null;
            while (true)
            {
                switch (instance.NodeType)
                {
                    case ExpressionType.MemberAccess:
                        var memberExpression = ((MemberExpression)instance);
                        if (memberName == null)
                        {
                            memberName = memberExpression.Member.Name;
                        }
                        instance = memberExpression.Expression;
                        break;
                    case ExpressionType.Parameter:
                        var parameterExpression = ((ParameterExpression)instance);
                        if (memberName == null)
                        {
                            memberName = parameterExpression.Name;
                        }
                        return memberName == _queryRootName;
                    default:
                        return false;
                }
            }
        }

        private string GetPropertyName(string name, Type exprType)
        {
            var memberInfo = (MemberInfo)exprType.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) ??
                exprType.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            if (memberInfo == null)
            {
                memberInfo = ReflectionUtil.GetPropertiesAndFieldsFor(exprType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(x => x.Name == name);
            }

            if (memberInfo != null)
            {
                foreach (var customAttribute in memberInfo.GetCustomAttributes(true))
                {
                    string propName;
                    var customAttributeType = customAttribute.GetType();
                    if (typeof(JsonPropertyAttribute).Namespace != customAttributeType.Namespace)
                        continue;
                    switch (customAttributeType.Name)
                    {
                        case "JsonPropertyAttribute":
                            propName = ((dynamic)customAttribute).PropertyName;
                            break;
                        case "DataMemberAttribute":
                            propName = ((dynamic)customAttribute).Name;
                            break;
                        default:
                            continue;
                    }
                    if (KeywordsInCSharp.Contains(propName))
                        return '@' + propName;
                    return propName ?? name;
                }
            }
            return name;
        }

        private static Type GetMemberType(MemberInfo member)
        {
            var prop = member as PropertyInfo;
            if (prop != null)
                return prop.PropertyType;
            return ((FieldInfo)member).FieldType;
        }

        /// <summary>
        ///   Returns a <see cref = "System.String" /> that represents this instance.
        /// </summary>
        /// <returns>
        ///   A <see cref = "System.String" /> that represents this instance.
        /// </returns>
        public override string ToString()
        {
            return _out.ToString();
        }

        private void SometimesParenthesis(ExpressionOperatorPrecedence outer, ExpressionOperatorPrecedence inner,
                                          Action visitor)
        {
            var needParenthesis = outer.NeedsParenthesisFor(inner);

            if (needParenthesis)
                Out("(");

            visitor();

            if (needParenthesis)
                Out(")");
        }

        private void Visit(Expression node, ExpressionOperatorPrecedence outerPrecedence)
        {
            var previous = _currentPrecedence;
            _currentPrecedence = outerPrecedence;
            Visit(node);
            _currentPrecedence = previous;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.BinaryExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitBinary(BinaryExpression node)
        {
            return VisitBinary(node, _currentPrecedence);
        }

        private Expression VisitBinary(BinaryExpression node, ExpressionOperatorPrecedence outerPrecedence)
        {
            ExpressionOperatorPrecedence innerPrecedence;

            string str;
            var leftOp = node.Left;
            var rightOp = node.Right;

            FixupEnumBinaryExpression(ref leftOp, ref rightOp);
            switch (node.NodeType)
            {
                case ExpressionType.Add:
                    str = "+";
                    innerPrecedence = ExpressionOperatorPrecedence.Additive;
                    break;

                case ExpressionType.AddChecked:
                    str = "+";
                    innerPrecedence = ExpressionOperatorPrecedence.Additive;
                    break;

                case ExpressionType.And:
                    if ((node.Type != typeof(bool)) && (node.Type != typeof(bool?)))
                    {
                        str = "&";
                        innerPrecedence = ExpressionOperatorPrecedence.LogicalAND;
                    }
                    else
                    {
                        str = "And";
                        innerPrecedence = ExpressionOperatorPrecedence.ConditionalAND;
                    }
                    break;

                case ExpressionType.AndAlso:
                    str = "&&";
                    innerPrecedence = ExpressionOperatorPrecedence.ConditionalAND;
                    break;

                case ExpressionType.Coalesce:
                    str = "??";
                    innerPrecedence = ExpressionOperatorPrecedence.NullCoalescing;
                    break;

                case ExpressionType.Divide:
                    str = "/";
                    innerPrecedence = ExpressionOperatorPrecedence.Multiplicative;
                    break;

                case ExpressionType.Equal:
                    str = "==";
                    innerPrecedence = ExpressionOperatorPrecedence.Equality;
                    break;

                case ExpressionType.ExclusiveOr:
                    str = "^";
                    innerPrecedence = ExpressionOperatorPrecedence.LogicalXOR;
                    break;

                case ExpressionType.GreaterThan:
                    str = ">";
                    innerPrecedence = ExpressionOperatorPrecedence.RelationalAndTypeTesting;
                    break;

                case ExpressionType.GreaterThanOrEqual:
                    str = ">=";
                    innerPrecedence = ExpressionOperatorPrecedence.RelationalAndTypeTesting;
                    break;

                case ExpressionType.LeftShift:
                    str = "<<";
                    innerPrecedence = ExpressionOperatorPrecedence.Shift;
                    break;

                case ExpressionType.LessThan:
                    str = "<";
                    innerPrecedence = ExpressionOperatorPrecedence.RelationalAndTypeTesting;
                    break;

                case ExpressionType.LessThanOrEqual:
                    str = "<=";
                    innerPrecedence = ExpressionOperatorPrecedence.RelationalAndTypeTesting;
                    break;

                case ExpressionType.Modulo:
                    str = "%";
                    innerPrecedence = ExpressionOperatorPrecedence.Multiplicative;
                    break;

                case ExpressionType.Multiply:
                    str = "*";
                    innerPrecedence = ExpressionOperatorPrecedence.Multiplicative;
                    break;

                case ExpressionType.MultiplyChecked:
                    str = "*";
                    innerPrecedence = ExpressionOperatorPrecedence.Multiplicative;
                    break;

                case ExpressionType.NotEqual:
                    str = "!=";
                    innerPrecedence = ExpressionOperatorPrecedence.RelationalAndTypeTesting;
                    break;

                case ExpressionType.Or:
                    if ((node.Type != typeof(bool)) && (node.Type != typeof(bool?)))
                    {
                        str = "|";
                        innerPrecedence = ExpressionOperatorPrecedence.LogicalOR;
                    }
                    else
                    {
                        str = "Or";
                        innerPrecedence = ExpressionOperatorPrecedence.LogicalOR;
                    }
                    break;

                case ExpressionType.OrElse:
                    str = "||";
                    innerPrecedence = ExpressionOperatorPrecedence.ConditionalOR;
                    break;

                case ExpressionType.Power:
                    str = "^";
                    innerPrecedence = ExpressionOperatorPrecedence.LogicalXOR;
                    break;

                case ExpressionType.RightShift:
                    str = ">>";
                    innerPrecedence = ExpressionOperatorPrecedence.Shift;
                    break;

                case ExpressionType.Subtract:
                    str = "-";
                    innerPrecedence = ExpressionOperatorPrecedence.Additive;
                    break;

                case ExpressionType.SubtractChecked:
                    str = "-";
                    innerPrecedence = ExpressionOperatorPrecedence.Additive;
                    break;

                case ExpressionType.Assign:
                    str = "=";
                    innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    break;

                case ExpressionType.AddAssign:
                    str = "+=";
                    innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    break;

                case ExpressionType.AndAssign:
                    if ((node.Type != typeof(bool)) && (node.Type != typeof(bool?)))
                    {
                        str = "&=";
                        innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    }
                    else
                    {
                        str = "&&=";
                        innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    }
                    break;

                case ExpressionType.DivideAssign:
                    str = "/=";
                    innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    break;

                case ExpressionType.ExclusiveOrAssign:
                    str = "^=";
                    innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    break;

                case ExpressionType.LeftShiftAssign:
                    str = "<<=";
                    innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    break;

                case ExpressionType.ModuloAssign:
                    str = "%=";
                    innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    break;

                case ExpressionType.MultiplyAssign:
                    str = "*=";
                    innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    break;

                case ExpressionType.OrAssign:
                    if ((node.Type != typeof(bool)) && (node.Type != typeof(bool?)))
                    {
                        str = "|=";
                        innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    }
                    else
                    {
                        str = "||=";
                        innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    }
                    break;

                case ExpressionType.PowerAssign:
                    str = "**=";
                    innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    break;

                case ExpressionType.RightShiftAssign:
                    str = ">>=";
                    innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    break;

                case ExpressionType.SubtractAssign:
                    str = "-=";
                    innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    break;

                case ExpressionType.AddAssignChecked:
                    str = "+=";
                    innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    break;

                case ExpressionType.MultiplyAssignChecked:
                    str = "*=";
                    innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    break;

                case ExpressionType.SubtractAssignChecked:
                    str = "-=";
                    innerPrecedence = ExpressionOperatorPrecedence.Assignment;
                    break;

                case ExpressionType.ArrayIndex:

                    innerPrecedence = ExpressionOperatorPrecedence.Primary;

                    SometimesParenthesis(outerPrecedence, innerPrecedence, delegate
                    {
                        Visit(leftOp, innerPrecedence);
                        Out("[");
                        Visit(rightOp, innerPrecedence);
                        Out("]");
                    });
                    return node;

                default:
                    throw new InvalidOperationException();
            }

            SometimesParenthesis(outerPrecedence, innerPrecedence, delegate
            {
                if (innerPrecedence == ExpressionOperatorPrecedence.NullCoalescing && TypeExistsOnServer(rightOp.Type))
                {
                    Out("((");
                    Out(ConvertTypeToCSharpKeyword(rightOp.Type, out _));
                    Out(")(");
                }
                Visit(leftOp, innerPrecedence);
                Out(' ');
                Out(str);
                Out(' ');
                Visit(rightOp, innerPrecedence);
                if (innerPrecedence == ExpressionOperatorPrecedence.NullCoalescing && TypeExistsOnServer(rightOp.Type))
                {
                    Out("))");
                }
            });

            return node;
        }

        private void FixupEnumBinaryExpression(ref Expression left, ref Expression right)
        {
            switch (left.NodeType)
            {
                case ExpressionType.ConvertChecked:
                case ExpressionType.Convert:
                    var leftWithoutConvert = ((UnaryExpression)left).Operand;
                    var enumType = Nullable.GetUnderlyingType(leftWithoutConvert.Type) ?? leftWithoutConvert.Type;
                    if (enumType.IsEnum == false)
                        return;

                    var rightWithoutConvert = SkipConvertExpressions(right);

                    if (rightWithoutConvert is ConstantExpression constantExpression)
                    {
                        left = leftWithoutConvert;

                        if (constantExpression.Value == null)
                        {
                            right = Expression.Constant(null);
                        }
                        else
                        {
                            right = _conventions.SaveEnumsAsIntegers
                                ? Expression.Constant(Convert.ToInt32(constantExpression.Value))
                                : Expression.Constant(Enum.ToObject(enumType, constantExpression.Value).ToString());
                        }
                    }
                    else
                    {
                        if (leftWithoutConvert is MemberExpression && rightWithoutConvert is MemberExpression)
                        {
                            var rightType = Nullable.GetUnderlyingType(rightWithoutConvert.Type) ?? rightWithoutConvert.Type;

                            if (rightType.IsEnum)
                            {
                                left = leftWithoutConvert;
                                right = rightWithoutConvert;
                            }
                        }
                    }

                    break;
            }

            while (true)
            {
                switch (left.NodeType)
                {
                    case ExpressionType.ConvertChecked:
                    case ExpressionType.Convert:
                        left = ((UnaryExpression)left).Operand;
                        break;
                    default:
                        return;
                }
            }
        }

        private Expression SkipConvertExpressions(Expression expression)
        {
            switch (expression.NodeType)
            {
                case ExpressionType.ConvertChecked:
                case ExpressionType.Convert:
                    return SkipConvertExpressions(((UnaryExpression)expression).Operand);
                default:
                    return expression;
            }
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.BlockExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitBlock(BlockExpression node)
        {
            Out("{");
            foreach (var expression in node.Variables)
            {
                Out("var ");
                Visit(expression);
                Out(";");
            }
            Out(" ... }");
            return node;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.CatchBlock" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override CatchBlock VisitCatchBlock(CatchBlock node)
        {
            Out("catch (" + node.Test.Name);
            if (node.Variable != null)
            {
                Out(node.Variable.Name);
            }
            Out(") { ... }");
            return node;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.ConditionalExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitConditional(ConditionalExpression node)
        {
            return VisitConditional(node, _currentPrecedence);
        }

        private Expression VisitConditional(ConditionalExpression node, ExpressionOperatorPrecedence outerPrecedence)
        {
            const ExpressionOperatorPrecedence innerPrecedence = ExpressionOperatorPrecedence.Conditional;

            SometimesParenthesis(outerPrecedence, innerPrecedence, delegate
            {
                Visit(node.Test, innerPrecedence);
                Out(" ? ");
                Visit(node.IfTrue, innerPrecedence);
                Out(" : ");
                Visit(node.IfFalse, innerPrecedence);
            });

            return node;
        }

        /// <summary>
        ///   Visits the <see cref = "T:System.Linq.Expressions.ConstantExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitConstant(ConstantExpression node)
        {
            if (node.Value == null)
            {
                // Avoid converting/casting the type, if it already converted/casted.
                if (_currentPrecedence == ExpressionOperatorPrecedence.Assignment)
                    ConvertTypeToCSharpKeywordIncludeNullable(node.Type);
                Out("null");
                return node;
            }

            var s = Convert.ToString(node.Value, CultureInfo.InvariantCulture);
            if (node.Value is string)
            {
                Out("\"");
                OutLiteral((string)node.Value);
                Out("\"");
                return node;
            }
            if (node.Value is bool)
            {
                Out(node.Value.ToString().ToLower());
                return node;
            }
            if (node.Value is char)
            {
                Out("'");
                OutLiteral((char)node.Value);
                Out("'");
                return node;
            }
            if (node.Value is Enum)
            {
                var enumType = node.Value.GetType();
                if (_insideWellKnownType || TypeExistsOnServer(enumType))
                {
                    var name = _insideWellKnownType
                        ? enumType.Name
                        : enumType.FullName;

                    Out(name.Replace("+", "."));
                    Out('.');
                    Out(s);
                    return node;
                }
                if (_conventions.SaveEnumsAsIntegers)
                    Out((Convert.ToInt32(node.Value)).ToString());
                else
                {
                    Out('"');
                    Out(node.Value.ToString());
                    Out('"');
                }
                return node;
            }
            if (node.Value is decimal)
            {
                Out(s);
                Out('M');
                return node;
            }
            Out(s);
            return node;
        }

        private void OutLiteral(string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            StringExtensions.EscapeString(_out, value);
        }



        private void OutLiteral(char c)
        {
            StringExtensions.EscapeChar(_out, c);
        }

        private void ConvertTypeToCSharpKeywordIncludeNullable(Type type)
        {
            var nonNullableType = Nullable.GetUnderlyingType(type);
            type = nonNullableType ?? type;
            var isNullableType = nonNullableType != null;

            // we only cast enums and types is mscorlib. We don't support anything else
            // because the VB compiler like to put converts all over the place, and include
            // types that we can't really support (only exists on the client)
            if (ShouldConvert(type) == false)
                return;

            Out("(");
            Out(ConvertTypeToCSharpKeyword(type, out var isValueTypeServerSide));

            if (isNullableType && nonNullableType != typeof(Guid) && isValueTypeServerSide)
            {
                Out("?");
            }
            Out(")");
        }

        private string ConvertTypeToCSharpKeyword(Type type, out bool isValueTypeOnTheServerSide)
        {
            isValueTypeOnTheServerSide = true;
            if (type.IsGenericType)
            {
                if (TypeExistsOnServer(type) == false)
                    throw new InvalidOperationException("Cannot make use of type " + type + " because it is a generic type that doesn't exists on the server");
                var typeDefinition = type.GetGenericTypeDefinition();
                var sb = new StringBuilder(typeDefinition.FullName, 0)
                {
                    Length = typeDefinition.FullName.IndexOf('`')
                };
                sb.Replace('+', '.');
                sb.Append("<");

                var arguments = type.GetGenericArguments();
                for (int i = 0; i < arguments.Length; i++)
                {
                    if (i != 0)
                        sb.Append(", ");
                    sb.Append(ConvertTypeToCSharpKeyword(arguments[i], out _));
                }

                sb.Append(">");
                isValueTypeOnTheServerSide = type.IsValueType;// KeyValuePair<K,V>
                return sb.ToString();
            }

            if (type == typeof(Guid) || type == typeof(Guid?))
            {
                // on the server, Guids are represented as strings
                return "string";
            }
            if (type == typeof(char))
            {
                return "char";
            }
            if (type == typeof(bool))
            {
                return "bool";
            }
            if (type == typeof(bool?))
            {
                return "bool?";
            }
            if (type == typeof(decimal))
            {
                return "decimal";
            }
            if (type == typeof(decimal?))
            {
                return "decimal?";
            }
            if (type == typeof(double))
            {
                return "double";
            }
            if (type == typeof(double?))
            {
                return "double?";
            }
            if (type == typeof(float))
            {
                return "float";
            }
            if (type == typeof(float?))
            {
                return "float?";
            }
            if (type == typeof(long))
            {
                return "long";
            }
            if (type == typeof(long?))
            {
                return "long?";
            }
            if (type == typeof(int))
            {
                return "int";
            }
            if (type == typeof(int?))
            {
                return "int?";
            }
            if (type == typeof(short))
            {
                return "short";
            }
            if (type == typeof(short?))
            {
                return "short?";
            }
            if (type == typeof(byte))
            {
                return "byte";
            }
            if (type == typeof(byte?))
            {
                return "byte?";
            }

            isValueTypeOnTheServerSide = false;

            if (type.IsEnum)
            {
                return "string";
            }
            if (type == typeof(string))
            {
                return "string";
            }
            if (type.FullName == "System.Object")
            {
                return "object";
            }
            const string knownNamespace = "System";
            if (_insideWellKnownType || knownNamespace == type.Namespace)
            {
                isValueTypeOnTheServerSide = type.IsValueType;
                return type.Name;
            }
            return type.FullName;
        }

        private bool TypeExistsOnServer(Type type) => TypeExistsOnServer(type, false);

        private bool TypeExistsOnServer(Type type, bool isGenericArgument)
        {
            if (_insideWellKnownType)
                return true;

            if (type.IsGenericType)
            {
                foreach (Type genericArgument in type.GetGenericArguments())
                {
                    if (TypeExistsOnServer(genericArgument, true) == false)
                        return false;
                }
            }

            if (type.IsEnum && isGenericArgument) // enum is known type when it is a generic argument
                return true;

            if (type.Assembly == typeof(HashSet<>).Assembly) // System.Core
                return true;

            if (type.Assembly == typeof(object).Assembly) // mscorlib
                return true;

            if (type.Assembly == typeof(Uri).Assembly) // System assembly
                return true;

            if (type.Assembly == typeof(Regex).Assembly) // System.Text.RegularExpressions
                return true;

            if (type.Assembly.FullName.StartsWith("Lucene.Net") &&
                type.Assembly.FullName.Contains("PublicKeyToken=85089178b9ac3181"))
                return true;

            return _conventions.TypeIsKnownServerSide(type);
        }

        /// <summary>
        ///   Visits the <see cref = "T:System.Linq.Expressions.DebugInfoExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitDebugInfo(DebugInfoExpression node)
        {
            var s = string.Format(CultureInfo.CurrentCulture, "<DebugInfo({0}: {1}, {2}, {3}, {4})>", node.Document.FileName, node.StartLine, node.StartColumn, node.EndLine, node.EndColumn);
            Out(s);
            return node;
        }

        /// <summary>
        ///   Visits the <see cref = "T:System.Linq.Expressions.DefaultExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitDefault(DefaultExpression node)
        {
            Out("default(");

            var nonNullable = Nullable.GetUnderlyingType(node.Type);
            Out(ConvertTypeToCSharpKeyword(nonNullable ?? node.Type, out var isValueTypeServerSide));
            if (nonNullable != null && nonNullable != typeof(Guid) && isValueTypeServerSide)
                Out("?");

            Out(")");
            return node;
        }

        /// <summary>
        ///   Visits the element init.
        /// </summary>
        /// <param name = "initializer">The initializer.</param>
        /// <returns></returns>
        protected override ElementInit VisitElementInit(ElementInit initializer)
        {
            Out(initializer.AddMethod.ToString());
            VisitExpressions('(', initializer.Arguments, ')');
            return initializer;
        }

        private void VisitExpressions<T>(char open, IEnumerable<T> expressions, char close) where T : Expression
        {
            Out(open);
            if (expressions != null)
            {
                var flag = true;
                foreach (var local in expressions)
                {
                    if (flag)
                    {
                        flag = false;
                    }
                    else
                    {
                        Out(", ");
                    }
                    Visit(local, ExpressionOperatorPrecedence.ParenthesisNotNeeded);
                }
            }
            Out(close);
        }

        /// <summary>
        ///   Visits the children of the extension expression.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitExtension(Expression node)
        {
            throw new NotImplementedException();

            /*const BindingFlags bindingAttr = BindingFlags.Public | BindingFlags.Instance;

            if (node.GetType().GetMethod("ToString", bindingAttr, null, ReflectionUtils.EmptyTypes, null).DeclaringType !=
                typeof(Expression))
            {
                Out(node.ToString());
                return node;
            }
            Out("[");
            Out(node.NodeType == ExpressionType.Extension ? node.GetType().FullName : node.NodeType.ToString());
            Out("]");
            return node;*/
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.GotoExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitGoto(GotoExpression node)
        {
            Out(node.Kind.ToString().ToLower());

            DumpLabel(node.Target);
            if (node.Value != null)
            {
                Out(" (");
                Visit(node.Value);
                Out(") ");
            }
            return node;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.IndexExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitIndex(IndexExpression node)
        {
            if (node.Object != null)
            {
                Visit(node.Object);
            }
            else
            {
                Out(node.Indexer.DeclaringType.Name);
            }
            if (node.Indexer != null)
            {
                Out(".");
                Out(node.Indexer.Name);
            }
            VisitExpressions('[', node.Arguments, ']');
            return node;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.InvocationExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitInvocation(InvocationExpression node)
        {
            Out("Invoke(");
            Visit(node.Expression);
            var num = 0;
            var count = node.Arguments.Count;
            while (num < count)
            {
                Out(", ");
                Visit(node.Arguments[num]);
                num++;
            }
            Out(")");
            return node;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.LabelExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitLabel(LabelExpression node)
        {
            Out("{ ... } ");
            DumpLabel(node.Target);
            Out(":");
            return node;
        }

        /// <summary>
        ///   Visits the lambda.
        /// </summary>
        /// <typeparam name = "T"></typeparam>
        /// <param name = "node">The node.</param>
        /// <returns></returns>
        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            if (node.Parameters.Count == 1)
            {
                Visit(node.Parameters[0]);
            }
            else
            {
                VisitExpressions('(', node.Parameters, ')');
            }
            Out(" => ");
            var body = node.Body;
            if (_castLambdas)
            {
                switch (body.NodeType)
                {
                    case ExpressionType.Convert:
                    case ExpressionType.ConvertChecked:
                        break;
                    default:
                        body = Expression.Convert(body, body.Type);
                        break;
                }
            }
            Visit(body);
            return node;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.ListInitExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitListInit(ListInitExpression node)
        {
            Visit(node.NewExpression);
            Out(" {");
            var num = 0;
            var count = node.Initializers.Count;
            while (num < count)
            {
                if (num > 0)
                {
                    Out(", ");
                }
                Out("{");
                bool first = true;
                foreach (var expression in node.Initializers[num].Arguments)
                {
                    if (first == false)
                        Out(", ");
                    first = false;
                    Visit(expression);
                }
                Out("}");
                num++;
            }
            Out("}");
            return node;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.LoopExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitLoop(LoopExpression node)
        {
            Out("loop { ... }");
            return node;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.MemberExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitMember(MemberExpression node)
        {
            if (Nullable.GetUnderlyingType(node.Member.DeclaringType) != null)
            {
                switch (node.Member.Name)
                {
                    case "HasValue":
                        // we don't have nullable type on the server side, we just compare to null
                        Out("(");
                        Visit(node.Expression);
                        Out(" != null)");
                        return node;
                    case "Value":
                        Visit(node.Expression);
                        return node; // we don't have nullable type on the server side, we can safely ignore this.
                }
            }
            var exprType = node.Expression != null ? node.Member.DeclaringType : node.Type;
#if FEATURE_DATEONLY_TIMEONLY_SUPPORT
            if (_isProjectionPart && (node.Type == typeof(TimeOnly) || node.Type == typeof(DateOnly) || node.Type == typeof(TimeOnly?) || node.Type == typeof(DateOnly?)))
            {
                if (Nullable.GetUnderlyingType(node.Type) is Type underlyingType)
                {
                    Out($"As{underlyingType.Name}(");
                }
                else
                {
                    Out($"As{node.Type.Name}(");
                }

                
                OutMember(node.Expression, node.Member, exprType);
                Out(")");
                return node;
            }
#endif
            OutMember(node.Expression, node.Member, exprType);
            return node;
        }

        /// <summary>
        ///   Visits the member assignment.
        /// </summary>
        /// <param name = "assignment">The assignment.</param>
        /// <returns></returns>
        protected override MemberAssignment VisitMemberAssignment(MemberAssignment assignment)
        {
            Out(assignment.Member.Name);
            Out(" = ");
            var constantExpression = assignment.Expression as ConstantExpression;
            if (constantExpression != null && constantExpression.Value == null)
            {
                var memberType = GetMemberType(assignment.Member);
                if (_insideWellKnownType || ShouldConvert(memberType))
                {
                    Visit(Expression.Convert(assignment.Expression, memberType));
                }
                else
                {
                    Out("(object)null");
                }
                return assignment;
            }
            Visit(assignment.Expression);
            return assignment;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.MemberInitExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitMemberInit(MemberInitExpression node)
        {
            var originalInsideWellKnownType = _insideWellKnownType;

            if (node.Type == typeof(CreateFieldOptions))
                _insideWellKnownType = true;

            if ((node.NewExpression.Arguments.Count == 0) && node.NewExpression.Type.Name.Contains("<"))
            {
                Out("new");
            }
            else
            { 
                Visit(node.NewExpression);
                if (TypeExistsOnServer(node.Type) == false)
                {
                    const int removeLength = 2;
                    _out.Remove(_out.Length - removeLength, removeLength);
                }
            }
            Out(" {");
            var num = 0;
            var count = node.Bindings.Count;
            while (num < count)
            {
                var binding = node.Bindings[num];
                if (num > 0)
                {
                    Out(", ");
                }
                VisitMemberBinding(binding);
                num++;
            }
            Out("}");

            if (node.Type == typeof(CreateFieldOptions))
                _insideWellKnownType = originalInsideWellKnownType;

            return node;
        }

        /// <summary>
        ///   Visits the member list binding.
        /// </summary>
        /// <param name = "binding">The binding.</param>
        /// <returns></returns>
        protected override MemberListBinding VisitMemberListBinding(MemberListBinding binding)
        {
            Out(binding.Member.Name);
            Out(" = {");
            var num = 0;
            var count = binding.Initializers.Count;
            while (num < count)
            {
                if (num > 0)
                {
                    Out(", ");
                }
                VisitElementInit(binding.Initializers[num]);
                num++;
            }
            Out("}");
            return binding;
        }

        /// <summary>
        ///   Visits the member member binding.
        /// </summary>
        /// <param name = "binding">The binding.</param>
        /// <returns></returns>
        protected override MemberMemberBinding VisitMemberMemberBinding(MemberMemberBinding binding)
        {
            Out(binding.Member.Name);
            Out(" = {");
            var num = 0;
            var count = binding.Bindings.Count;
            while (num < count)
            {
                if (num > 0)
                {
                    Out(", ");
                }
                VisitMemberBinding(binding.Bindings[num]);
                num++;
            }
            Out("}");
            return binding;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.MethodCallExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var constantExpression = node.Object as ConstantExpression;
            if (constantExpression != null && node.Type == typeof(Delegate))
            {
                var methodInfo = constantExpression.Value as MethodInfo;
                if (methodInfo != null && methodInfo.DeclaringType == typeof(AbstractCommonApiForIndexes))// a delegate call
                {
                    Out("((Func<");
                    for (int i = 0; i < methodInfo.GetParameters().Length; i++)
                    {
                        Out("dynamic, ");
                    }
                    Out("dynamic>)(");
                    if (methodInfo.Name == nameof(ILoadCommonApiForIndexes.LoadDocument))
                    {
                        var type = methodInfo.GetGenericArguments()[0];

                        Out($"k1 => {methodInfo.Name}(k1, \"");
                        OutLiteral(_conventions.GetCollectionName(type));
                        Out("\")");
                    }
                    else
                    {
                        Out(methodInfo.Name);
                    }

                    Out("))");
                    return node;
                }
            }

            if (node.Method.Name == "GetValueOrDefault" && Nullable.GetUnderlyingType(node.Method.DeclaringType) != null)
            {
                Visit(node.Object);
                return node; // we don't do anything here on the server
            }

            var isExtension = false;
            var num = 0;
            var expression = node.Object;

            var isDictionaryArgument = false;
            var isDictionaryObject = false;
            var isDictionaryReturn = false;
            var isConvertToDictionary = false;

            if (node.Object != null)
            {
                if (node.Object.Type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)))
                {
                    isDictionaryObject = true;
                }
            }

            if (node.Arguments.Count > 0 && node.Arguments[0].Type.IsGenericType)
            {
                if (node.Arguments[0].Type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)))
                    isDictionaryArgument = true;
            }

            if (node.Method.ReturnType.IsGenericType)
            {
                if (node.Method.ReturnType.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)))
                    isDictionaryReturn = true;
            }

            var shouldConvertToDynamicEnumerable = node.Method.IsStatic && ShouldConvertToDynamicEnumerable(node);
            if (shouldConvertToDynamicEnumerable == false && IsExtensionMethod(node))
            {
                num = 1;
                expression = node.Arguments[0];
                isExtension = true;
            }

            if (isDictionaryArgument && isDictionaryReturn)
            {
                if (isExtension)
                {
                    // TODO: remove if unnecessary
                }
                else
                {
                    num = 1;
                    expression = node.Arguments[0];
                    Visit(expression);
                    Out(".");
                }
            }
            else if (isDictionaryArgument)
            {
                if (isExtension)
                {
                    Visit(expression);

                    if (expression is MethodCallExpression == false)
                        Out(".ToDictionary(e1 => e1.Key, e1 => e1.Value)");
                }
                else
                {
                    if (node.Method.DeclaringType.Name == "Enumerable")
                    {
                        num = 1;
                        expression = node.Arguments[0];

                        Visit(expression);

                        if (expression is MethodCallExpression == false)
                            Out(".ToDictionary(e1 => e1.Key, e1 => e1.Value)");
                    }
                    else
                    {
                        Out(node.Method.DeclaringType.Name);
                        isConvertToDictionary = true;
                    }
                }
                Out(".");
            }
            else if (isDictionaryReturn)
            {
                if (isExtension)
                {
                    // TODO: remove if unnecessary
                }
                else
                {
                    num = 1;
                    expression = node.Arguments[0];
                    Visit(expression);
                    Out(".");
                }
            }
            else
            {
                if (expression != null)
                {
                    if (typeof(AbstractCommonApiForIndexes).IsAssignableFrom(expression.Type))
                    {
                        // this is a method that
                        // exists on both the server side and the client side
                        Out("this");
                    }
                    else if (typeof(NoTrackingCommonApiForIndexes).IsAssignableFrom(expression.Type))
                    {
                        Out("this.");
                        Out(nameof(AbstractCommonApiForIndexes.NoTracking));
                    }
                    else if (isDictionaryObject)
                    {
                        Visit(expression);
                        if (node.Method.Name != "get_Item")
                            Out(".ToDictionary(e1 => e1.Key, e1 => e1.Value)");
                    }
                    else
                    {
                        Visit(expression);
                    }
                    if (IsIndexerCall(node) == false)
                    {
                        Out(".");
                    }
                }

                if (shouldConvertToDynamicEnumerable)
                {
                    Out("DynamicEnumerable.");
                }
                else if (node.Method.IsStatic && isExtension == false)
                {
                    if (node.Method.DeclaringType == typeof(Enumerable) && node.Method.Name == "Cast")
                    {
                        Out("new DynamicArray(");
                        Visit(node.Arguments[0]);
                        Out(")");
                        return node; // we don't do casting on the server
                    }

                    Out(node.Method.DeclaringType.Name);
                    Out(".");
                }
            }

            if (IsIndexerCall(node))
            {
                Out("[");
            }
            else
            {
                switch (node.Method.Name)
                {
                    case "First":
                        Out("FirstOrDefault");
                        break;
                    case "Last":
                        Out("LastOrDefault");
                        break;
                    case "Single":
                        Out("SingleOrDefault");
                        break;
                    case "ElementAt":
                        Out("ElementAtOrDefault");
                        break;
                    // Convert OfType<Foo>() to Where(x => x["$type"] == typeof(Foo).AssemblyQualifiedName)
                    case "OfType":
                        if (JavascriptConversionExtensions.LinqMethodsSupport
                            .IsDictionary(node.Arguments[0].Type))
                        {
                            _isDictionary = true;
                            goto default;
                        }
                        Out("Where");
                        break;
                    case nameof(ILoadCommonApiForIndexes.LoadDocument):
                        Out(nameof(ILoadCommonApiForIndexes.LoadDocument));
                        break;
                    case nameof(ILoadCompareExchangeApiForIndexes.LoadCompareExchangeValue):
                        Out(nameof(ILoadCompareExchangeApiForIndexes.LoadCompareExchangeValue));
                        break;
                    default:
                        Out(node.Method.Name);
                        if (node.Method.IsGenericMethod)
                        {
                            OutputGenericMethodArgumentsIfNeeded(node.Method);
                        }
                        break;
                }
                Out("(");
            }
            var num2 = num;
            var count = node.Arguments.Count;
            while (num2 < count)
            {
                if (num2 > num)
                {
                    Out(", ");
                }
                var old = _castLambdas;
                try
                {
                    switch (node.Method.Name)
                    {
                        case "Sum":
                        case "Average":
                        case "Min":
                        case "Max":
                            _castLambdas = true;
                            break;
                        default:
                            _castLambdas = false;
                            break;
                    }

                    var oldAvoidDuplicateParameters = _avoidDuplicatedParameters;
                    var oldIsSelectMany = _isSelectMany;
                    var oldIsProjectionPart = _isProjectionPart;
                    _isSelectMany = node.Method.Name == "SelectMany";
                    if (node.Method.Name == "Select" || _isSelectMany)
                    {
                        _avoidDuplicatedParameters = true;
                        _isProjectionPart = true;
                    }

                    if (node.Arguments[num2].NodeType == ExpressionType.MemberAccess)
                    {
                        var methodArgType = node.Method.GetParameters()[num2].ParameterType;
                        if (methodArgType.IsPrimitive || methodArgType == typeof(string))
                        {
                            // now we need to figure out if this method has overloads,
                            // for example, we may call Convert.ToInt64(Int64), but we want to
                            // compile on the server to Convert.ToInt64(object);
                            if (node.Method.DeclaringType.GetMethods().Count(m => node.Method.Name == m.Name) == 1)
                                Out("(" + methodArgType.FullName + ")");
                        }
                    }

                    Visit(node.Arguments[num2]);

                    if (isConvertToDictionary)
                        Out(".ToDictionary(eg => eg.Key, or => or.Value)");

                    _isSelectMany = oldIsSelectMany;
                    _avoidDuplicatedParameters = oldAvoidDuplicateParameters;
                    _isProjectionPart = oldIsProjectionPart;
                }
                finally
                {
                    _castLambdas = old;
                }
                num2++;
            }

            // Convert OfType<Foo>() to Where(x => x["$type"] == typeof(Foo).AssemblyQualifiedName)
            if (node.Method.Name == "OfType" && _isDictionary == false)
            {
                var type = node.Method.GetGenericArguments()[0];
                var typeFullName = ReflectionUtil.GetFullNameWithoutVersionInformation(type);
                Out("_itemRaven => string.Equals(_itemRaven[\"$type\"], \"");
                Out(typeFullName);
                Out("\", StringComparison.Ordinal)");
            }

            if (node.Method.Name == nameof(ILoadCommonApiForIndexes.LoadDocument))
            {
                var type = node.Method.GetGenericArguments()[0];
                _loadDocumentTypes.Add(type);

                var parameters = node.Method.GetParameters();
                if (parameters.Length == 1)
                {
                    Out($", \"");
                    OutLiteral(_conventions.GetCollectionName(type));
                    Out("\"");
                }
                else if (parameters.Length == 2)
                {
                    // collection name was provided - validate it - LoadDocument<T>("id", "CollectionName")

                    var argument = node.Arguments[1];
                    if (argument.NodeType != ExpressionType.Constant)
                        throw new InvalidOperationException($"Invalid argument in {nameof(ILoadCommonApiForIndexes.LoadDocument)}. String constant was expected but was '{argument.NodeType}' with value '{argument}'.");
                }
                else
                {
                    throw new NotSupportedException($"Unknown overload of {nameof(ILoadCommonApiForIndexes.LoadDocument)} method");
                }
            }

            Out(IsIndexerCall(node) ? "]" : ")");

            if (node.Type.IsValueType &&
                TypeExistsOnServer(node.Type) &&
                node.Type.Name != typeof(KeyValuePair<,>).Name)
            {
                switch (node.Method.Name)
                {
                    case "First":
                    case "FirstOrDefault":
                    case "Last":
                    case "LastOrDefault":
                    case "Single":
                    case "SingleOrDefault":
                    case "ElementAt":
                    case "ElementAtOrDefault":
                        Out(" ?? ");
                        VisitDefault(Expression.Default(node.Type));
                        break;
                }
            }
            return node;
        }

        private void OutputGenericMethodArgumentsIfNeeded(MethodInfo method)
        {
            var genericArguments = method.GetGenericArguments();
            if (genericArguments.All(TypeExistsOnServer) == false)
                return; // no point if the types aren't on the server

            if (_isDictionary == false)
            {
                switch (method.DeclaringType.Name)
                {
                    case "Enumerable":
                    case "Queryable":
                        return; // we don't need those, we have LinqOnDynamic for it
                }
            }

            Out("<");
            bool first = true;
            foreach (var genericArgument in genericArguments)
            {
                if (first == false)
                {
                    Out(", ");
                }
                first = false;

                VisitType(genericArgument);
            }
            Out(">");
        }

        private static bool IsIndexerCall(MethodCallExpression node)
        {
            return node.Method.IsSpecialName && (node.Method.Name.StartsWith("get_") || node.Method.Name.StartsWith("set_"));
        }

        private bool ShouldConvertToDynamicEnumerable(MethodCallExpression node)
        {
            var declaringType = node.Method.DeclaringType;
            if (declaringType == null)
                return false;
            if (declaringType.Name == "Enumerable")
            {
                switch (node.Method.Name)
                {
                    case "First":
                    case "FirstOrDefault":
                    case "Single":
                    case "SingleOrDefault":
                    case "Last":
                    case "LastOrDefault":
                    case "ElementAt":
                    case "ElementAtOrDefault":
                    case "Min":
                    case "Max":
                    case "Union":
                    case "Concat":
                    case "Intersect":
                    case nameof(Enumerable.Distinct):
                        return true;
                    case nameof(Enumerable.OrderBy):
                    case nameof(Enumerable.OrderByDescending):
                        return _isReduce;
                }
            }

            return false;
        }

        private static bool IsExtensionMethod(MethodCallExpression node)
        {
            var attribute = node.Method.GetCustomAttribute(typeof(ExtensionAttribute));
            if (attribute == null)
                return false;

            if (node.Method.DeclaringType.Name == "Enumerable")
            {
                switch (node.Method.Name)
                {
                    case "Select":
                    case "SelectMany":
                    case "Where":
                    case "GroupBy":
                    case "OrderBy":
                    case "OrderByDescending":
                    case "ThenBy":
                    case "ThenByDescending":
                    case "DefaultIfEmpty":
                    case "Reverse":
                    case "Take":
                    case "Skip":
                    case "TakeWhile":
                    case "SkipWhile":
                    case "OfType":
                        return true;
                }
                return false;
            }

            if (node.Method.GetCustomAttributes(typeof(RavenMethodAttribute), false).Count() != 0)
                return false;

            return true;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.NewExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitNew(NewExpression node)
        {
            Out("new ");
            if (TypeExistsOnServer(node.Type))
            {
                VisitType(node.Type);
                Out("(");
            }
            else
            {
                Out("{");
            }
            for (var i = 0; i < node.Arguments.Count; i++)
            {
                if (i > 0)
                {
                    Out(", ");
                }

                var argument = node.Arguments[i];

                if (node.Members?[i] != null)
                {
                    string name = node.Members[i].Name;
                    name = KeywordsInCSharp.Contains(name) ? $"@{name}" : name;
                    Out(name);
                    Out(" = ");

                    if (argument is ConstantExpression constantExpression && constantExpression.Value == null)
                    {
                        Out("(");
                        VisitType(GetMemberType(node.Members[i]));
                        Out(")");
                    }
                }
                else if (TypeExistsOnServer(argument.Type) && IsEnum(argument.Type) == false)
                {
                    Out("(");
                    VisitType(argument.Type);
                    Out(")");
                }

                Visit(argument);
            }

            Out(TypeExistsOnServer(node.Type) ? ")" : "}");

            return node;

            bool IsEnum(Type type)
            {
                var isEnum = type.IsEnum;
                if (isEnum == false)
                {
                    var nonNullableType = Nullable.GetUnderlyingType(type);
                    if (nonNullableType != null)
                        isEnum = nonNullableType.IsEnum;
                }

                return isEnum;
            }
        }

        private void VisitType(Type type)
        {
            if (type.IsGenericType == false || CheckIfAnonymousType(type))
            {
                if (type.IsArray)
                {
                    VisitType(type.GetElementType());
                    Out("[");
                    for (int i = 0; i < type.GetArrayRank() - 1; i++)
                    {
                        Out(",");
                    }
                    Out("]");
                    return;
                }
                var nonNullableType = Nullable.GetUnderlyingType(type);
                if (nonNullableType != null)
                {
                    VisitType(nonNullableType);
                    Out("?");
                    return;
                }
                Out(ConvertTypeToCSharpKeyword(type, out _));
                return;
            }
            var genericArguments = type.GetGenericArguments();
            var genericTypeDefinition = type.GetGenericTypeDefinition();
            var lastIndexOfTag = genericTypeDefinition.FullName.LastIndexOf('`');

            Out(genericTypeDefinition.FullName.Substring(0, lastIndexOfTag));
            Out("<");
            bool first = true;
            foreach (var genericArgument in genericArguments)
            {
                if (first == false)
                    Out(", ");
                first = false;
                VisitType(genericArgument);
            }
            Out(">");
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.NewArrayExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitNewArray(NewArrayExpression node)
        {
            switch (node.NodeType)
            {
                case ExpressionType.NewArrayInit:
                    Out("new ");
                    OutputAppropriateArrayType(node);
                    Out("[]");
                    VisitExpressions('{', node.Expressions, '}');
                    return node;

                case ExpressionType.NewArrayBounds:
                    if (TypeExistsOnServer(node.Type))
                        Out("new " + node.Type.GetElementType());
                    else
                        Out("new object");
                    VisitExpressions('[', node.Expressions, ']');
                    return node;
            }
            return node;
        }

        private void OutputAppropriateArrayType(NewArrayExpression node)
        {
            if (!CheckIfAnonymousType(node.Type.GetElementType()) && TypeExistsOnServer(node.Type.GetElementType()))
            {
                Out(ConvertTypeToCSharpKeyword(node.Type.GetElementType(), out _));
            }
            else
            {
                switch (node.NodeType)
                {
                    case ExpressionType.NewArrayInit:
                        if (node.Expressions.Count == 0)
                        {
                            Out("object");
                        }
                        break;
                    case ExpressionType.NewArrayBounds:
                        Out("object");
                        break;
                }
            }
        }

        private static bool CheckIfAnonymousType(Type type)
        {
            // hack: the only way to detect anonymous types right now
            return type.IsDefined(typeof(CompilerGeneratedAttribute), false)
                && type.IsGenericType && type.Name.Contains("AnonymousType")
                && (type.Name.StartsWith("<>") || type.Name.StartsWith("VB$"))
                && type.Attributes.HasFlag(TypeAttributes.NotPublic);
        }

        public static readonly HashSet<string> KeywordsInCSharp = new HashSet<string>(new[]
        {
            "abstract",
            "as",
            "base",
            "bool",
            "break",
            "byte",
            "case",
            "catch",
            "char",
            "checked",
            "class",
            "const",
            "continue",
            "decimal",
            "default",
            "delegate",
            "do",
            "double",
            "else",
            "enum",
            "event",
            "explicit",
            "extern",
            "false",
            "finally",
            "fixed",
            "float",
            "for",
            "foreach",
            "goto",
            "if",
            "implicit",
            "in",
            "in (generic modifier)",
            "int",
            "interface",
            "internal",
            "is",
            "lock",
            "long",
            "namespace",
            "new",
            "null",
            "object",
            "operator",
            "out",
            "out (generic modifier)",
            "override",
            "params",
            "private",
            "protected",
            "public",
            "readonly",
            "ref",
            "return",
            "sbyte",
            "sealed",
            "short",
            "sizeof",
            "stackalloc",
            "static",
            "string",
            "struct",
            "switch",
            "this",
            "throw",
            "true",
            "try",
            "typeof",
            "uint",
            "ulong",
            "unchecked",
            "unsafe",
            "ushort",
            "using",
            "virtual",
            "void",
            "volatile",
            "while"
        });

        private bool _insideWellKnownType;
        private bool _avoidDuplicatedParameters;
        private bool _isSelectMany;
        private bool _isProjectionPart;
        private readonly HashSet<Type> _loadDocumentTypes = new HashSet<Type>();

        /// <summary>
        ///   Visits the <see cref = "T:System.Linq.Expressions.ParameterExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node.IsByRef)
            {
                Out("ref ");
            }
            if (string.IsNullOrEmpty(node.Name))
            {
                Out("Param_" + GetParamId(node));
                return node;
            }

            var name = node.Name;
            if (_avoidDuplicatedParameters)
            {
                object other;
                if (_isSelectMany == false &&
                    _duplicatedParams.TryGetValue(name, out other) &&
                    ReferenceEquals(other, node) == false)
                {
                    name += GetParamId(node);
                    _duplicatedParams[name] = node;
                }
                else
                {
                    _duplicatedParams[name] = node;
                }
            }
            name = name.StartsWith("$VB$") ? name.Substring(4) : name;
            if (KeywordsInCSharp.Contains(name))
                Out('@');
            Out(name);
            return node;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.RuntimeVariablesExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitRuntimeVariables(RuntimeVariablesExpression node)
        {
            VisitExpressions('(', node.Variables, ')');
            return node;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.SwitchExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitSwitch(SwitchExpression node)
        {
            Out("switch ");
            Out("(");
            Visit(node.SwitchValue);
            Out(") { ... }");
            return node;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.SwitchCase" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override SwitchCase VisitSwitchCase(SwitchCase node)
        {
            Out("case ");
            VisitExpressions('(', node.TestValues, ')');
            Out(": ...");
            return node;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.TryExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitTry(TryExpression node)
        {
            Out("try { ... }");
            return node;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.TypeBinaryExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitTypeBinary(TypeBinaryExpression node)
        {
            const ExpressionOperatorPrecedence currentPrecedence = ExpressionOperatorPrecedence.RelationalAndTypeTesting;
            string op;
            switch (node.NodeType)
            {
                case ExpressionType.TypeIs:
                    op = " is ";
                    break;

                case ExpressionType.TypeEqual:
                    op = " TypeEqual ";
                    break;
                default:
                    throw new InvalidOperationException();
            }

            Visit(node.Expression, currentPrecedence);
            Out(op);
            Out(node.TypeOperand.Name);
            return node;
        }

        /// <summary>
        ///   Visits the children of the <see cref = "T:System.Linq.Expressions.UnaryExpression" />.
        /// </summary>
        /// <param name = "node">The expression to visit.</param>
        /// <returns>
        ///   The modified expression, if it or any subexpression was modified; otherwise, returns the original expression.
        /// </returns>
        protected override Expression VisitUnary(UnaryExpression node)
        {
            return VisitUnary(node, _currentPrecedence);
        }

        private Expression VisitUnary(UnaryExpression node, ExpressionOperatorPrecedence outerPrecedence)
        {
            var innerPrecedence = ExpressionOperatorPrecedence.Unary;

            switch (node.NodeType)
            {
                case ExpressionType.TypeAs:
                    innerPrecedence = ExpressionOperatorPrecedence.RelationalAndTypeTesting;
                    break;

                case ExpressionType.Decrement:
                    innerPrecedence = ExpressionOperatorPrecedence.ParenthesisNotNeeded;
                    Out("Decrement(");
                    break;

                case ExpressionType.Negate:
                case ExpressionType.NegateChecked:
                    Out("-");
                    break;

                case ExpressionType.UnaryPlus:
                    Out("+");
                    break;

                case ExpressionType.Not:
                    Out("!");
                    break;

                case ExpressionType.Quote:
                    break;

                case ExpressionType.Increment:
                    innerPrecedence = ExpressionOperatorPrecedence.ParenthesisNotNeeded;
                    Out("Increment(");
                    break;

                case ExpressionType.Throw:
                    innerPrecedence = ExpressionOperatorPrecedence.ParenthesisNotNeeded;
                    Out("throw ");
                    break;

                case ExpressionType.PreIncrementAssign:
                    Out("++");
                    break;

                case ExpressionType.PreDecrementAssign:
                    Out("--");
                    break;

                case ExpressionType.OnesComplement:
                    Out("~");
                    break;
                case ExpressionType.Convert:
                case ExpressionType.ConvertChecked:
                    if (node.Method != null && node.Method.Name == "Parse" && node.Method.DeclaringType == typeof(DateTime))
                    {
                        Out(node.Method.DeclaringType.Name);
                        Out(".Parse(");
                    }
                    else if (node.Type != typeof(object) || node.Operand.NodeType == ExpressionType.Constant)
                    {
                        Out("(");
                        ConvertTypeToCSharpKeywordIncludeNullable(node.Type);
                    }
                    break;
                case ExpressionType.ArrayLength:
                    // we don't want to do nothing for those
                    Out("(");
                    break;
                default:
                    innerPrecedence = ExpressionOperatorPrecedence.ParenthesisNotNeeded;
                    Out(node.NodeType.ToString());
                    Out("(");
                    break;
            }

            SometimesParenthesis(outerPrecedence, innerPrecedence, () => Visit(node.Operand, innerPrecedence));

            switch (node.NodeType)
            {
                case ExpressionType.TypeAs:
                    Out(" As ");
                    Out(node.Type.Name);
                    break;

                case ExpressionType.ArrayLength:
                    Out(".Length)");
                    break;

                case ExpressionType.Decrement:
                case ExpressionType.Increment:
                    Out(")");
                    break;

                case ExpressionType.Convert:
                case ExpressionType.ConvertChecked:
                    if (node.Type != typeof(object) || node.Operand.NodeType == ExpressionType.Constant)
                        Out(")");
                    break;

                case ExpressionType.Negate:
                case ExpressionType.UnaryPlus:
                case ExpressionType.NegateChecked:
                case ExpressionType.Not:
                case ExpressionType.Quote:
                case ExpressionType.Throw:
                case ExpressionType.PreIncrementAssign:
                case ExpressionType.PreDecrementAssign:
                case ExpressionType.OnesComplement:
                    break;

                case ExpressionType.PostIncrementAssign:
                    Out("++");
                    break;

                case ExpressionType.PostDecrementAssign:
                    Out("--");
                    break;

                default:
                    Out(")");
                    break;
            }

            return node;
        }

        private bool ShouldConvert(Type nonNullableType)
        {
            if (_insideWellKnownType)
                return false;

            if (nonNullableType.IsEnum)
                return true;

            return nonNullableType.Assembly == typeof(string).Assembly && (nonNullableType.IsGenericType == false);
        }
    }
}
