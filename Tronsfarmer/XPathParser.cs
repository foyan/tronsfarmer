using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using XPathNodeType = System.Xml.XPath.XPathNodeType;
using System.Globalization;


namespace Tronsfarmer {

    class XPathTreeBuilder : IXPathBuilder<XElement> {

        public void StartBuild() { }

        public XElement EndBuild(XElement result) {
            return result;
        }

        public XElement String(string value) {
            return new XElement("string", new XAttribute("value", value));
        }

        public XElement Number(string value) {
            return new XElement("number", new XAttribute("value", value));
        }

        public XElement Operator(XPathOperator op, XElement left, XElement right) {
            if (op == XPathOperator.UnaryMinus) {
                return new XElement("negate", left);
            }
            return new XElement(op.ToString(), left, right);
        }

        public XElement Axis(XPathAxis xpathAxis, XPathNodeType nodeType, string prefix, string name) {
            return new XElement(xpathAxis.ToString(),
                new XAttribute("nodeType", nodeType.ToString()),
                new XAttribute("prefix", prefix ?? "(null)"),
                new XAttribute("name", name ?? "(null)")
            );
        }

        public XElement JoinStep(XElement left, XElement right) {
            return new XElement("step", left, right);
        }

        public XElement Predicate(XElement node, XElement condition, bool reverseStep) {
            return new XElement("predicate", new XAttribute("reverse", reverseStep),
                node, condition
            );
        }

        public XElement Variable(string prefix, string name) {
            return new XElement("variable",
                new XAttribute("prefix", prefix ?? "(null)"),
                new XAttribute("name", name ?? "(null)")
            );
        }

        public XElement Function(string prefix, string name, IList<XElement> args) {
            XElement xe = new XElement("variable",
                new XAttribute("prefix", prefix ?? "(null)"),
                new XAttribute("name", name ?? "(null)")
            );
            foreach (XElement e in args) {
                xe.Add(e);
            }
            return xe;
        }
    }
    
    public interface IXPathBuilder<Node> {
        // Should be called once per build
        void StartBuild();

        // Should be called after build for result tree post-processing
        Node EndBuild(Node result);

        Node String(string value);

        Node Number(string value);

        Node Operator(XPathOperator op, Node left, Node right);

        Node Axis(XPathAxis xpathAxis, XPathNodeType nodeType, string prefix, string name);

        Node JoinStep(Node left, Node right);

        // http://www.w3.org/TR/xquery-semantics/#id-axis-steps
        // reverseStep is how parser comunicates to builder diference between "ansestor[1]" and "(ansestor)[1]" 
        Node Predicate(Node node, Node condition, bool reverseStep);

        Node Variable(string prefix, string name);

        Node Function(string prefix, string name, IList<Node> args);
    }


    public enum XPathAxis {
        Unknown = 0,
        Ancestor,
        AncestorOrSelf,
        Attribute,
        Child,
        Descendant,
        DescendantOrSelf,
        Following,
        FollowingSibling,
        Namespace,
        Parent,
        Preceding,
        PrecedingSibling,
        Self,
        Root,
    }

    public enum XPathOperator {
        Unknown = 0,
        Or,
        And,
        Eq,
        Ne,
        Lt,
        Le,
        Gt,
        Ge,
        Plus,
        Minus,
        Multiply,
        Divide,
        Modulo,
        UnaryMinus,
        Union
    }

    public class XPathParser<Node> {
        private XPathScanner scanner;
        private IXPathBuilder<Node> builder;
        private Stack<int> posInfo = new Stack<int>();

        // Six possible causes of exceptions in the builder:
        // 1. Undefined prefix in a node test.
        // 2. Undefined prefix in a variable reference, or unknown variable.
        // 3. Undefined prefix in a function call, or unknown function, or wrong number/types of arguments.
        // 4. Argument of Union operator is not a node-set.
        // 5. First argument of Predicate is not a node-set.
        // 6. Argument of Axis is not a node-set.

        public Node Parse(string xpathExpr, IXPathBuilder<Node> builder) {
            Debug.Assert(this.scanner == null && this.builder == null);
            Debug.Assert(builder != null);

            Node result = default(Node);
            this.scanner = new XPathScanner(xpathExpr);
            this.builder = builder;
            this.posInfo.Clear();

            try {
                builder.StartBuild();
                result = ParseExpr();
                scanner.CheckToken(LexKind.Eof);
            } catch (XPathParserException e) {
                if (e.queryString == null) {
                    e.queryString = scanner.Source;
                    PopPosInfo(out e.startChar, out e.endChar);
                }
                throw;
            } finally {
                result = builder.EndBuild(result);
#if DEBUG
                this.builder = null;
                this.scanner = null;
#endif
            }
            Debug.Assert(posInfo.Count == 0, "PushPosInfo() and PopPosInfo() calls have been unbalanced");
            return result;
        }

        #region Location paths and node tests

        /**************************************************************************************************/
        /*  Location paths and node tests                                                                 */
        /**************************************************************************************************/

        private static bool IsStep(LexKind lexKind) {
            return (
                       lexKind == LexKind.Dot ||
                       lexKind == LexKind.DotDot ||
                       lexKind == LexKind.At ||
                       lexKind == LexKind.Axis ||
                       lexKind == LexKind.Star ||
                       lexKind == LexKind.Name // NodeTest is also Name
                   );
        }

        /*
        *   LocationPath ::= RelativeLocationPath | '/' RelativeLocationPath? | '//' RelativeLocationPath
        */

        private Node ParseLocationPath() {
            if (scanner.Kind == LexKind.Slash) {
                scanner.NextLex();
                Node opnd = builder.Axis(XPathAxis.Root, XPathNodeType.All, null, null);

                if (IsStep(scanner.Kind)) {
                    opnd = builder.JoinStep(opnd, ParseRelativeLocationPath());
                }
                return opnd;
            } else if (scanner.Kind == LexKind.SlashSlash) {
                scanner.NextLex();
                return builder.JoinStep(
                    builder.Axis(XPathAxis.Root, XPathNodeType.All, null, null),
                    builder.JoinStep(
                        builder.Axis(XPathAxis.DescendantOrSelf, XPathNodeType.All, null, null),
                        ParseRelativeLocationPath()
                        )
                    );
            } else {
                return ParseRelativeLocationPath();
            }
        }

        /*
        *   RelativeLocationPath ::= Step (('/' | '//') Step)*
        */

        private Node ParseRelativeLocationPath() {
            Node opnd = ParseStep();
            if (scanner.Kind == LexKind.Slash) {
                scanner.NextLex();
                opnd = builder.JoinStep(opnd, ParseRelativeLocationPath());
            } else if (scanner.Kind == LexKind.SlashSlash) {
                scanner.NextLex();
                opnd = builder.JoinStep(opnd,
                                        builder.JoinStep(
                                            builder.Axis(XPathAxis.DescendantOrSelf, XPathNodeType.All, null, null),
                                            ParseRelativeLocationPath()
                                            )
                    );
            }
            return opnd;
        }

        /*
        *   Step ::= '.' | '..' | (AxisName '::' | '@')? NodeTest Predicate*
        */

        private Node ParseStep() {
            Node opnd;
            if (LexKind.Dot == scanner.Kind) {
                // '.'
                scanner.NextLex();
                opnd = builder.Axis(XPathAxis.Self, XPathNodeType.All, null, null);
                if (LexKind.LBracket == scanner.Kind) {
                    throw scanner.PredicateAfterDotException();
                }
            } else if (LexKind.DotDot == scanner.Kind) {
                // '..'
                scanner.NextLex();
                opnd = builder.Axis(XPathAxis.Parent, XPathNodeType.All, null, null);
                if (LexKind.LBracket == scanner.Kind) {
                    throw scanner.PredicateAfterDotDotException();
                }
            } else {
                // (AxisName '::' | '@')? NodeTest Predicate*
                XPathAxis axis;
                switch (scanner.Kind) {
                    case LexKind.Axis: // AxisName '::'
                        axis = scanner.Axis;
                        scanner.NextLex();
                        scanner.NextLex();
                        break;
                    case LexKind.At: // '@'
                        axis = XPathAxis.Attribute;
                        scanner.NextLex();
                        break;
                    case LexKind.Name:
                    case LexKind.Star:
                        // NodeTest must start with Name or '*'
                        axis = XPathAxis.Child;
                        break;
                    default:
                        throw scanner.UnexpectedTokenException(scanner.RawValue);
                }

                opnd = ParseNodeTest(axis);

                while (LexKind.LBracket == scanner.Kind) {
                    opnd = builder.Predicate(opnd, ParsePredicate(), IsReverseAxis(axis));
                }
            }
            return opnd;
        }

        private static bool IsReverseAxis(XPathAxis axis) {
            return (
                       axis == XPathAxis.Ancestor || axis == XPathAxis.Preceding ||
                       axis == XPathAxis.AncestorOrSelf || axis == XPathAxis.PrecedingSibling
                   );
        }

        /*
        *   NodeTest ::= NameTest | ('comment' | 'text' | 'node') '(' ')' | 'processing-instruction' '('  Literal? ')'
        *   NameTest ::= '*' | NCName ':' '*' | QName
        */

        private Node ParseNodeTest(XPathAxis axis) {
            XPathNodeType nodeType;
            string nodePrefix, nodeName;

            int startChar = scanner.LexStart;
            InternalParseNodeTest(scanner, axis, out nodeType, out nodePrefix, out nodeName);
            PushPosInfo(startChar, scanner.PrevLexEnd);
            Node result = builder.Axis(axis, nodeType, nodePrefix, nodeName);
            PopPosInfo();
            return result;
        }

        private static bool IsNodeType(XPathScanner scanner) {
            return scanner.Prefix.Length == 0 && (
                                                     scanner.Name == "node" ||
                                                     scanner.Name == "text" ||
                                                     scanner.Name == "processing-instruction" ||
                                                     scanner.Name == "comment"
                                                 );
        }

        private static XPathNodeType PrincipalNodeType(XPathAxis axis) {
            return (
                       axis == XPathAxis.Attribute ? XPathNodeType.Attribute :
                           axis == XPathAxis.Namespace ? XPathNodeType.Namespace :
                               /*else*/                      XPathNodeType.Element
                   );
        }

        private static void InternalParseNodeTest(XPathScanner scanner, XPathAxis axis, out XPathNodeType nodeType, out string nodePrefix, out string nodeName) {
            switch (scanner.Kind) {
                case LexKind.Name:
                    if (scanner.CanBeFunction && IsNodeType(scanner)) {
                        nodePrefix = null;
                        nodeName = null;
                        switch (scanner.Name) {
                            case "comment":
                                nodeType = XPathNodeType.Comment;
                                break;
                            case "text":
                                nodeType = XPathNodeType.Text;
                                break;
                            case "node":
                                nodeType = XPathNodeType.All;
                                break;
                            default:
                                Debug.Assert(scanner.Name == "processing-instruction");
                                nodeType = XPathNodeType.ProcessingInstruction;
                                break;
                        }

                        scanner.NextLex();
                        scanner.PassToken(LexKind.LParens);

                        if (nodeType == XPathNodeType.ProcessingInstruction) {
                            if (scanner.Kind != LexKind.RParens) {
                                // 'processing-instruction' '(' Literal ')'
                                scanner.CheckToken(LexKind.String);
                                // It is not needed to set nodePrefix here, but for our current implementation
                                // comparing whole QNames is faster than comparing just local names
                                nodePrefix = string.Empty;
                                nodeName = scanner.StringValue;
                                scanner.NextLex();
                            }
                        }

                        scanner.PassToken(LexKind.RParens);
                    } else {
                        nodePrefix = scanner.Prefix;
                        nodeName = scanner.Name;
                        nodeType = PrincipalNodeType(axis);
                        scanner.NextLex();
                        if (nodeName == "*") {
                            nodeName = null;
                        }
                    }
                    break;
                case LexKind.Star:
                    nodePrefix = null;
                    nodeName = null;
                    nodeType = PrincipalNodeType(axis);
                    scanner.NextLex();
                    break;
                default:
                    throw scanner.NodeTestExpectedException(scanner.RawValue);
            }
        }

        /*
        *   Predicate ::= '[' Expr ']'
        */

        private Node ParsePredicate() {
            scanner.PassToken(LexKind.LBracket);
            Node opnd = ParseExpr();
            scanner.PassToken(LexKind.RBracket);
            return opnd;
        }

        #endregion

        #region Expressions

        /**************************************************************************************************/
        /*  Expressions                                                                                   */
        /**************************************************************************************************/

        /*
        *   Expr   ::= OrExpr
        *   OrExpr ::= AndExpr ('or' AndExpr)*
        *   AndExpr ::= EqualityExpr ('and' EqualityExpr)*
        *   EqualityExpr ::= RelationalExpr (('=' | '!=') RelationalExpr)*
        *   RelationalExpr ::= AdditiveExpr (('<' | '>' | '<=' | '>=') AdditiveExpr)*
        *   AdditiveExpr ::= MultiplicativeExpr (('+' | '-') MultiplicativeExpr)*
        *   MultiplicativeExpr ::= UnaryExpr (('*' | 'div' | 'mod') UnaryExpr)*
        *   UnaryExpr ::= ('-')* UnionExpr
        */

        private Node ParseExpr() {
            return ParseSubExpr( /*callerPrec:*/0);
        }

        private Node ParseSubExpr(int callerPrec) {
            XPathOperator op;
            Node opnd;

            // Check for unary operators
            if (scanner.Kind == LexKind.Minus) {
                op = XPathOperator.UnaryMinus;
                int opPrec = XPathOperatorPrecedence[(int) op];
                scanner.NextLex();
                opnd = builder.Operator(op, ParseSubExpr(opPrec), default(Node));
            } else {
                opnd = ParseUnionExpr();
            }

            // Process binary operators
            while (true) {
                op = (scanner.Kind <= LexKind.LastOperator) ? (XPathOperator) scanner.Kind : XPathOperator.Unknown;
                int opPrec = XPathOperatorPrecedence[(int) op];
                if (opPrec <= callerPrec)
                    return opnd;

                // Operator's precedence is greater than the one of our caller, so process it here
                scanner.NextLex();
                opnd = builder.Operator(op, opnd, ParseSubExpr( /*callerPrec:*/opPrec));
            }
        }

        private static int[] XPathOperatorPrecedence = {
            /*Unknown    */ 0,
            /*Or         */ 1,
            /*And        */ 2,
            /*Eq         */ 3,
            /*Ne         */ 3,
            /*Lt         */ 4,
            /*Le         */ 4,
            /*Gt         */ 4,
            /*Ge         */ 4,
            /*Plus       */ 5,
            /*Minus      */ 5,
            /*Multiply   */ 6,
            /*Divide     */ 6,
            /*Modulo     */ 6,
            /*UnaryMinus */ 7,
            /*Union      */ 8, // Not used
        };

        /*
        *   UnionExpr ::= PathExpr ('|' PathExpr)*
        */

        private Node ParseUnionExpr() {
            int startChar = scanner.LexStart;
            Node opnd1 = ParsePathExpr();

            if (scanner.Kind == LexKind.Union) {
                PushPosInfo(startChar, scanner.PrevLexEnd);
                opnd1 = builder.Operator(XPathOperator.Union, default(Node), opnd1);
                PopPosInfo();

                while (scanner.Kind == LexKind.Union) {
                    scanner.NextLex();
                    startChar = scanner.LexStart;
                    Node opnd2 = ParsePathExpr();
                    PushPosInfo(startChar, scanner.PrevLexEnd);
                    opnd1 = builder.Operator(XPathOperator.Union, opnd1, opnd2);
                    PopPosInfo();
                }
            }
            return opnd1;
        }

        /*
        *   PathExpr ::= LocationPath | FilterExpr (('/' | '//') RelativeLocationPath )?
        */

        private Node ParsePathExpr() {
            // Here we distinguish FilterExpr from LocationPath - the former starts with PrimaryExpr
            if (IsPrimaryExpr()) {
                int startChar = scanner.LexStart;
                Node opnd = ParseFilterExpr();
                int endChar = scanner.PrevLexEnd;

                if (scanner.Kind == LexKind.Slash) {
                    scanner.NextLex();
                    PushPosInfo(startChar, endChar);
                    opnd = builder.JoinStep(opnd, ParseRelativeLocationPath());
                    PopPosInfo();
                } else if (scanner.Kind == LexKind.SlashSlash) {
                    scanner.NextLex();
                    PushPosInfo(startChar, endChar);
                    opnd = builder.JoinStep(opnd,
                                            builder.JoinStep(
                                                builder.Axis(XPathAxis.DescendantOrSelf, XPathNodeType.All, null, null),
                                                ParseRelativeLocationPath()
                                                )
                        );
                    PopPosInfo();
                }
                return opnd;
            } else {
                return ParseLocationPath();
            }
        }

        /*
        *   FilterExpr ::= PrimaryExpr Predicate*
        */

        private Node ParseFilterExpr() {
            int startChar = scanner.LexStart;
            Node opnd = ParsePrimaryExpr();
            int endChar = scanner.PrevLexEnd;

            while (scanner.Kind == LexKind.LBracket) {
                PushPosInfo(startChar, endChar);
                opnd = builder.Predicate(opnd, ParsePredicate(), /*reverseStep:*/false);
                PopPosInfo();
            }
            return opnd;
        }

        private bool IsPrimaryExpr() {
            return (
                       scanner.Kind == LexKind.String ||
                       scanner.Kind == LexKind.Number ||
                       scanner.Kind == LexKind.Dollar ||
                       scanner.Kind == LexKind.LParens ||
                       scanner.Kind == LexKind.Name && scanner.CanBeFunction && !IsNodeType(scanner)
                   );
        }

        /*
        *   PrimaryExpr ::= Literal | Number | VariableReference | '(' Expr ')' | FunctionCall
        */

        private Node ParsePrimaryExpr() {
            Debug.Assert(IsPrimaryExpr());
            Node opnd;
            switch (scanner.Kind) {
                case LexKind.String:
                    opnd = builder.String(scanner.StringValue);
                    scanner.NextLex();
                    break;
                case LexKind.Number:
                    opnd = builder.Number(scanner.RawValue);
                    scanner.NextLex();
                    break;
                case LexKind.Dollar:
                    int startChar = scanner.LexStart;
                    scanner.NextLex();
                    scanner.CheckToken(LexKind.Name);
                    PushPosInfo(startChar, scanner.LexStart + scanner.LexSize);
                    opnd = builder.Variable(scanner.Prefix, scanner.Name);
                    PopPosInfo();
                    scanner.NextLex();
                    break;
                case LexKind.LParens:
                    scanner.NextLex();
                    opnd = ParseExpr();
                    scanner.PassToken(LexKind.RParens);
                    break;
                default:
                    Debug.Assert(
                        scanner.Kind == LexKind.Name && scanner.CanBeFunction && !IsNodeType(scanner),
                        "IsPrimaryExpr() returned true, but the lexeme is not recognized"
                        );
                    opnd = ParseFunctionCall();
                    break;
            }
            return opnd;
        }

        /*
        *   FunctionCall ::= FunctionName '(' (Expr (',' Expr)* )? ')'
        */

        private Node ParseFunctionCall() {
            List<Node> argList = new List<Node>();
            string name = scanner.Name;
            string prefix = scanner.Prefix;
            int startChar = scanner.LexStart;

            scanner.PassToken(LexKind.Name);
            scanner.PassToken(LexKind.LParens);

            if (scanner.Kind != LexKind.RParens) {
                while (true) {
                    argList.Add(ParseExpr());
                    if (scanner.Kind != LexKind.Comma) {
                        scanner.CheckToken(LexKind.RParens);
                        break;
                    }
                    scanner.NextLex(); // move off the ','
                }
            }

            scanner.NextLex(); // move off the ')'
            PushPosInfo(startChar, scanner.PrevLexEnd);
            Node result = builder.Function(prefix, name, argList);
            PopPosInfo();
            return result;
        }

        #endregion

        /**************************************************************************************************/
        /*  Helper methods                                                                                */
        /**************************************************************************************************/

        private void PushPosInfo(int startChar, int endChar) {
            posInfo.Push(startChar);
            posInfo.Push(endChar);
        }

        private void PopPosInfo() {
            posInfo.Pop();
            posInfo.Pop();
        }

        private void PopPosInfo(out int startChar, out int endChar) {
            endChar = posInfo.Pop();
            startChar = posInfo.Pop();
        }

        private static double ToDouble(string str) {
            double d;
            if (double.TryParse(str, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowTrailingWhite, NumberFormatInfo.InvariantInfo, out d)) {
                return d;
            }
            return double.NaN;
        }
    }


    public class XPathParserException : System.Exception {
        public string queryString;
        public int startChar;
        public int endChar;

        public XPathParserException(string queryString, int startChar, int endChar, string message)
            : base(message) {
            this.queryString = queryString;
            this.startChar = startChar;
            this.endChar = endChar;
        }

        private enum TrimType {
            Left,
            Right,
            Middle,
        }

        // This function is used to prevent long quotations in error messages
        private static void AppendTrimmed(StringBuilder sb, string value, int startIndex, int count, TrimType trimType) {
            const int TrimSize = 32;
            const string TrimMarker = "...";

            if (count <= TrimSize) {
                sb.Append(value, startIndex, count);
            } else {
                switch (trimType) {
                    case TrimType.Left:
                        sb.Append(TrimMarker);
                        sb.Append(value, startIndex + count - TrimSize, TrimSize);
                        break;
                    case TrimType.Right:
                        sb.Append(value, startIndex, TrimSize);
                        sb.Append(TrimMarker);
                        break;
                    case TrimType.Middle:
                        sb.Append(value, startIndex, TrimSize/2);
                        sb.Append(TrimMarker);
                        sb.Append(value, startIndex + count - TrimSize/2, TrimSize/2);
                        break;
                }
            }
        }

        internal string MarkOutError() {
            if (queryString == null || queryString.Trim(' ').Length == 0) {
                return null;
            }

            int len = endChar - startChar;
            StringBuilder sb = new StringBuilder();

            AppendTrimmed(sb, queryString, 0, startChar, TrimType.Left);
            if (len > 0) {
                sb.Append(" -->");
                AppendTrimmed(sb, queryString, startChar, len, TrimType.Middle);
            }

            sb.Append("<-- ");
            AppendTrimmed(sb, queryString, endChar, queryString.Length - endChar, TrimType.Right);

            return sb.ToString();
        }


        private string FormatDetailedMessage() {
            string message = Message;
            string error = MarkOutError();

            if (error != null && error.Length > 0) {
                if (message.Length > 0) {
                    message += Environment.NewLine;
                }
                message += error;
            }
            return message;
        }

        public override string ToString() {
            string result = this.GetType().FullName;
            string info = FormatDetailedMessage();
            if (info != null && info.Length > 0) {
                result += ": " + info;
            }
            if (StackTrace != null) {
                result += Environment.NewLine + StackTrace;
            }
            return result;
        }

    }

    internal enum LexKind {
        Unknown, // Unknown lexeme
        Or, // Operator 'or'
        And, // Operator 'and'
        Eq, // Operator '='
        Ne, // Operator '!='
        Lt, // Operator '<'
        Le, // Operator '<='
        Gt, // Operator '>'
        Ge, // Operator '>='
        Plus, // Operator '+'
        Minus, // Operator '-'
        Multiply, // Operator '*'
        Divide, // Operator 'div'
        Modulo, // Operator 'mod'
        UnaryMinus, // Not used
        Union, // Operator '|'
        LastOperator = Union,

        DotDot, // '..'
        ColonColon, // '::'
        SlashSlash, // Operator '//'
        Number, // Number (numeric literal)
        Axis, // AxisName

        Name, // NameTest, NodeType, FunctionName, AxisName, second part of VariableReference
        String, // Literal (string literal)
        Eof, // End of the expression

        FirstStringable = Name,
        LastNonChar = Eof,

        LParens = '(',
        RParens = ')',
        LBracket = '[',
        RBracket = ']',
        Dot = '.',
        At = '@',
        Comma = ',',

        Star = '*', // NameTest
        Slash = '/', // Operator '/'
        Dollar = '$', // First part of VariableReference
        RBrace = '}', // Used for AVTs
    };

    internal sealed class XPathScanner {
        private string xpathExpr;
        private int curIndex;
        private char curChar;
        private LexKind kind;
        private string name;
        private string prefix;
        private string stringValue;
        private bool canBeFunction;
        private int lexStart;
        private int prevLexEnd;
        private LexKind prevKind;
        private XPathAxis axis;

        public XPathScanner(string xpathExpr)
            : this(xpathExpr, 0) {
        }

        public XPathScanner(string xpathExpr, int startFrom) {
            Debug.Assert(xpathExpr != null);
            this.xpathExpr = xpathExpr;
            this.kind = LexKind.Unknown;
            SetSourceIndex(startFrom);
            NextLex();
        }

        public string Source {
            get { return xpathExpr; }
        }

        public LexKind Kind {
            get { return kind; }
        }

        public int LexStart {
            get { return lexStart; }
        }

        public int LexSize {
            get { return curIndex - lexStart; }
        }

        public int PrevLexEnd {
            get { return prevLexEnd; }
        }

        private void SetSourceIndex(int index) {
            Debug.Assert(0 <= index && index <= xpathExpr.Length);
            curIndex = index - 1;
            NextChar();
        }

        private void NextChar() {
            Debug.Assert(-1 <= curIndex && curIndex < xpathExpr.Length);
            curIndex++;
            if (curIndex < xpathExpr.Length) {
                curChar = xpathExpr[curIndex];
            } else {
                Debug.Assert(curIndex == xpathExpr.Length);
                curChar = '\0';
            }
        }

        public string Name {
            get {
                Debug.Assert(kind == LexKind.Name);
                Debug.Assert(name != null);
                return name;
            }
        }

        public string Prefix {
            get {
                Debug.Assert(kind == LexKind.Name);
                Debug.Assert(prefix != null);
                return prefix;
            }
        }

        public string RawValue {
            get {
                if (kind == LexKind.Eof) {
                    return LexKindToString(kind);
                } else {
                    return xpathExpr.Substring(lexStart, curIndex - lexStart);
                }
            }
        }

        public string StringValue {
            get {
                Debug.Assert(kind == LexKind.String);
                Debug.Assert(stringValue != null);
                return stringValue;
            }
        }

        // Returns true if the character following an QName (possibly after intervening
        // ExprWhitespace) is '('. In this case the token must be recognized as a NodeType
        // or a FunctionName unless it is an OperatorName. This distinction cannot be done
        // without knowing the previous lexeme. For example, "or" in "... or (1 != 0)" may
        // be an OperatorName or a FunctionName.
        public bool CanBeFunction {
            get {
                Debug.Assert(kind == LexKind.Name);
                return canBeFunction;
            }
        }

        public XPathAxis Axis {
            get {
                Debug.Assert(kind == LexKind.Axis);
                Debug.Assert(axis != XPathAxis.Unknown);
                return axis;
            }
        }

        private void SkipSpace() {
            while (IsWhiteSpace(curChar)) {
                NextChar();
            }
        }

        private static bool IsAsciiDigit(char ch) {
            return (uint) (ch - '0') <= 9;
        }

        public static bool IsWhiteSpace(char ch) {
            return ch <= ' ' && (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r');
        }

        public void NextLex() {
            prevLexEnd = curIndex;
            prevKind = kind;
            SkipSpace();
            lexStart = curIndex;

            switch (curChar) {
                case '\0':
                    kind = LexKind.Eof;
                    return;
                case '(':
                case ')':
                case '[':
                case ']':
                case '@':
                case ',':
                case '$':
                case '}':
                    kind = (LexKind) curChar;
                    NextChar();
                    break;
                case '.':
                    NextChar();
                    if (curChar == '.') {
                        kind = LexKind.DotDot;
                        NextChar();
                    } else if (IsAsciiDigit(curChar)) {
                        SetSourceIndex(lexStart);
                        goto case '0';
                    } else {
                        kind = LexKind.Dot;
                    }
                    break;
                case ':':
                    NextChar();
                    if (curChar == ':') {
                        kind = LexKind.ColonColon;
                        NextChar();
                    } else {
                        kind = LexKind.Unknown;
                    }
                    break;
                case '*':
                    kind = LexKind.Star;
                    NextChar();
                    CheckOperator(true);
                    break;
                case '/':
                    NextChar();
                    if (curChar == '/') {
                        kind = LexKind.SlashSlash;
                        NextChar();
                    } else {
                        kind = LexKind.Slash;
                    }
                    break;
                case '|':
                    kind = LexKind.Union;
                    NextChar();
                    break;
                case '+':
                    kind = LexKind.Plus;
                    NextChar();
                    break;
                case '-':
                    kind = LexKind.Minus;
                    NextChar();
                    break;
                case '=':
                    kind = LexKind.Eq;
                    NextChar();
                    break;
                case '!':
                    NextChar();
                    if (curChar == '=') {
                        kind = LexKind.Ne;
                        NextChar();
                    } else {
                        kind = LexKind.Unknown;
                    }
                    break;
                case '<':
                    NextChar();
                    if (curChar == '=') {
                        kind = LexKind.Le;
                        NextChar();
                    } else {
                        kind = LexKind.Lt;
                    }
                    break;
                case '>':
                    NextChar();
                    if (curChar == '=') {
                        kind = LexKind.Ge;
                        NextChar();
                    } else {
                        kind = LexKind.Gt;
                    }
                    break;
                case '"':
                case '\'':
                    kind = LexKind.String;
                    ScanString();
                    break;
                case '0':
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                    kind = LexKind.Number;
                    ScanNumber();
                    break;
                default:
                    this.name = ScanNCName();
                    if (this.name != null) {
                        kind = LexKind.Name;
                        this.prefix = string.Empty;
                        this.canBeFunction = false;
                        this.axis = XPathAxis.Unknown;
                        bool colonColon = false;
                        int saveSourceIndex = curIndex;

                        // "foo:bar" or "foo:*" -- one lexeme (no spaces allowed)
                        // "foo::" or "foo ::"  -- two lexemes, reported as one (AxisName)
                        // "foo:?" or "foo :?"  -- lexeme "foo" reported
                        if (curChar == ':') {
                            NextChar();
                            if (curChar == ':') {
                                // "foo::" -> OperatorName, AxisName
                                NextChar();
                                colonColon = true;
                                SetSourceIndex(saveSourceIndex);
                            } else {
                                // "foo:bar", "foo:*" or "foo:?"
                                string ncName = ScanNCName();
                                if (ncName != null) {
                                    this.prefix = this.name;
                                    this.name = ncName;
                                    // Look ahead for '(' to determine whether QName can be a FunctionName
                                    saveSourceIndex = curIndex;
                                    SkipSpace();
                                    this.canBeFunction = (curChar == '(');
                                    SetSourceIndex(saveSourceIndex);
                                } else if (curChar == '*') {
                                    NextChar();
                                    this.prefix = this.name;
                                    this.name = "*";
                                } else {
                                    // "foo:?" -> OperatorName, NameTest
                                    // Return "foo" and leave ":" to be reported later as an unknown lexeme
                                    SetSourceIndex(saveSourceIndex);
                                }
                            }
                        } else {
                            SkipSpace();
                            if (curChar == ':') {
                                // "foo ::" or "foo :?"
                                NextChar();
                                if (curChar == ':') {
                                    NextChar();
                                    colonColon = true;
                                }
                                SetSourceIndex(saveSourceIndex);
                            } else {
                                this.canBeFunction = (curChar == '(');
                            }
                        }
                        if (!CheckOperator(false) && colonColon) {
                            this.axis = CheckAxis();
                        }
                    } else {
                        kind = LexKind.Unknown;
                        NextChar();
                    }
                    break;
            }
        }

        private bool CheckOperator(bool star) {
            LexKind opKind;

            if (star) {
                opKind = LexKind.Multiply;
            } else {
                if (prefix.Length != 0 || name.Length > 3)
                    return false;

                switch (name) {
                    case "or":
                        opKind = LexKind.Or;
                        break;
                    case "and":
                        opKind = LexKind.And;
                        break;
                    case "div":
                        opKind = LexKind.Divide;
                        break;
                    case "mod":
                        opKind = LexKind.Modulo;
                        break;
                    default:
                        return false;
                }
            }

            // If there is a preceding token and the preceding token is not one of '@', '::', '(', '[', ',' or an Operator,
            // then a '*' must be recognized as a MultiplyOperator and an NCName must be recognized as an OperatorName.
            if (prevKind <= LexKind.LastOperator)
                return false;

            switch (prevKind) {
                case LexKind.Slash:
                case LexKind.SlashSlash:
                case LexKind.At:
                case LexKind.ColonColon:
                case LexKind.LParens:
                case LexKind.LBracket:
                case LexKind.Comma:
                case LexKind.Dollar:
                    return false;
            }

            this.kind = opKind;
            return true;
        }

        private XPathAxis CheckAxis() {
            this.kind = LexKind.Axis;
            switch (name) {
                case "ancestor":
                    return XPathAxis.Ancestor;
                case "ancestor-or-self":
                    return XPathAxis.AncestorOrSelf;
                case "attribute":
                    return XPathAxis.Attribute;
                case "child":
                    return XPathAxis.Child;
                case "descendant":
                    return XPathAxis.Descendant;
                case "descendant-or-self":
                    return XPathAxis.DescendantOrSelf;
                case "following":
                    return XPathAxis.Following;
                case "following-sibling":
                    return XPathAxis.FollowingSibling;
                case "namespace":
                    return XPathAxis.Namespace;
                case "parent":
                    return XPathAxis.Parent;
                case "preceding":
                    return XPathAxis.Preceding;
                case "preceding-sibling":
                    return XPathAxis.PrecedingSibling;
                case "self":
                    return XPathAxis.Self;
                default:
                    this.kind = LexKind.Name;
                    return XPathAxis.Unknown;
            }
        }

        private void ScanNumber() {
            Debug.Assert(IsAsciiDigit(curChar) || curChar == '.');
            while (IsAsciiDigit(curChar)) {
                NextChar();
            }
            if (curChar == '.') {
                NextChar();
                while (IsAsciiDigit(curChar)) {
                    NextChar();
                }
            }
            if ((curChar & (~0x20)) == 'E') {
                NextChar();
                if (curChar == '+' || curChar == '-') {
                    NextChar();
                }
                while (IsAsciiDigit(curChar)) {
                    NextChar();
                }
                throw ScientificNotationException();
            }
        }

        private void ScanString() {
            int startIdx = curIndex + 1;
            int endIdx = xpathExpr.IndexOf(curChar, startIdx);

            if (endIdx < 0) {
                SetSourceIndex(xpathExpr.Length);
                throw UnclosedStringException();
            }

            this.stringValue = xpathExpr.Substring(startIdx, endIdx - startIdx);
            SetSourceIndex(endIdx + 1);
        }

        private static Regex re = new Regex(@"\p{_xmlI}[\p{_xmlC}-[:]]*", RegexOptions.Compiled);

        private string ScanNCName() {
            Match m = re.Match(xpathExpr, curIndex);
            if (m.Success) {
                curIndex += m.Length - 1;
                NextChar();
                return m.Value;
            }
            return null;
        }

        public void PassToken(LexKind t) {
            CheckToken(t);
            NextLex();
        }

        public void CheckToken(LexKind t) {
            Debug.Assert(LexKind.FirstStringable <= t);
            if (kind != t) {
                if (t == LexKind.Eof) {
                    throw EofExpectedException(RawValue);
                } else {
                    throw TokenExpectedException(LexKindToString(t), RawValue);
                }
            }
        }

        // May be called for the following tokens: Name, String, Eof, Comma, LParens, RParens, LBracket, RBracket, RBrace
        private string LexKindToString(LexKind t) {
            Debug.Assert(LexKind.FirstStringable <= t);

            if (LexKind.LastNonChar < t) {
                Debug.Assert("()[].@,*/$}".IndexOf((char) t) >= 0);
                return new string((char) t, 1);
            }

            switch (t) {
                case LexKind.Name:
                    return "<name>";
                case LexKind.String:
                    return "<string literal>";
                case LexKind.Eof:
                    return "<eof>";
                default:
                    Debug.Fail("Unexpected LexKind: " + t.ToString());
                    return string.Empty;
            }
        }

        // XPath error messages
        // --------------------

        public XPathParserException UnexpectedTokenException(string token) {
            return new XPathParserException(xpathExpr, lexStart, curIndex,
                                            string.Format("Unexpected token '{0}' in the expression.", token)
                );
        }

        public XPathParserException NodeTestExpectedException(string token) {
            return new XPathParserException(xpathExpr, lexStart, curIndex,
                                            string.Format("Expected a node test, found '{0}'.", token)
                );
        }

        public XPathParserException PredicateAfterDotException() {
            return new XPathParserException(xpathExpr, lexStart, curIndex,
                                            "Abbreviated step '.' cannot be followed by a predicate. Use the full form 'self::node()[predicate]' instead."
                );
        }

        public XPathParserException PredicateAfterDotDotException() {
            return new XPathParserException(xpathExpr, lexStart, curIndex,
                                            "Abbreviated step '..' cannot be followed by a predicate. Use the full form 'parent::node()[predicate]' instead."
                );
        }

        public XPathParserException ScientificNotationException() {
            return new XPathParserException(xpathExpr, lexStart, curIndex,
                                            "Scientific notation is not allowed."
                );
        }

        public XPathParserException UnclosedStringException() {
            return new XPathParserException(xpathExpr, lexStart, curIndex,
                                            "String literal was not closed."
                );
        }

        public XPathParserException EofExpectedException(string token) {
            return new XPathParserException(xpathExpr, lexStart, curIndex,
                                            string.Format("Expected end of the expression, found '{0}'.", token)
                );
        }

        public XPathParserException TokenExpectedException(string expectedToken, string actualToken) {
            return new XPathParserException(xpathExpr, lexStart, curIndex,
                                            string.Format("Expected token '{0}', found '{1}'.", expectedToken, actualToken)
                );
        }
    }


}


