﻿using System;
using System.CodeDom;
using System.Text;

namespace IronAHK.Scripting
{
    partial class Parser
    {
        CodeStatement ParseFlow(CodeLine line, out bool rewind)
        {
            #region Variables

            string code = line.Code;

            char[] delimiters = new char[Spaces.Length + 2];
            delimiters[0] = Multicast;
            delimiters[1] = ParenOpen;
            Spaces.CopyTo(delimiters, 2);

            string[] parts = code.TrimStart(Spaces).Split(delimiters, 2);

            if (parts.Length > 1)
                parts[1] = parts[1].Trim(Spaces);

            int n;

            bool gosub = false;

            rewind = false;

            #endregion

            switch (parts[0].ToLowerInvariant())
            {
                #region If/Else

                case FlowIf:
                    {
                        if (parts.Length < 1)
                            throw new ParseException("If requires a parameter");

                        bool blockOpen = false;
                        CodeExpression condition = ParseFlowParameter(parts[1], true, out blockOpen);
                        CodeConditionStatement ifelse = new CodeConditionStatement();

                        var iftest = (CodeMethodInvokeExpression)InternalMethods.IfElse;
                        iftest.Parameters.Add(condition);

                        ifelse.Condition = iftest;
                        var block = new CodeBlock(line, Scope, ifelse.TrueStatements);
                        block.Type = blockOpen ? CodeBlock.BlockType.Within : CodeBlock.BlockType.Expect;
                        blocks.Push(block);

                        elses.Push(ifelse.FalseStatements);
                        return ifelse;
                    }

                case FlowElse:
                    {
                        if (elses.Count == 0)
                            throw new ParseException("Else with no preceeding if block");

                        bool blockOpen = parts.Length > 1 && parts[1][0] == BlockOpen;
                        var block = new CodeBlock(line, Scope, elses.Pop());
                        block.Type = blockOpen ? CodeBlock.BlockType.Within : CodeBlock.BlockType.Expect;

                        line.Code = line.Code.TrimStart(Spaces).Substring(FlowElse.Length).TrimStart(Spaces);
                        rewind = line.Code.Length != 0;

                        // don't push block for if/else chains (unless braces are used)
                        if (rewind && !line.Code.Split(Spaces)[0].Equals(FlowIf, StringComparison.OrdinalIgnoreCase))
                            blocks.Push(block);
                    }
                    break;

                #endregion

                #region Goto

                case FlowGosub:
                    gosub = true;
                    goto case FlowGoto;

                case FlowGoto:
                    if (parts.Length < 1)
                        throw new ParseException("No label specified");
                    if (parts[1].IndexOf(Resolve) != 1)
                        throw new ParseException("Dynamic label references are not supported"); // TODO: dynamic goto label references
                    else if (!IsIdentifier(parts[1]))
                        throw new ParseException("Illegal character in label name");
                    return new CodeGotoStatement(parts[1]); // TODO: gosub

                #endregion

                #region Loops

                case FlowLoop:
                    {
                        bool blockOpen = false;
                        CodeMethodInvokeExpression iterator;
                        bool skip = false;

                        if (parts.Length > 0)
                        {
                            switch (parts[0].ToUpperInvariant())
                            {
                                case "READ":
                                    skip = true;
                                    iterator = (CodeMethodInvokeExpression)InternalMethods.LoopRead;
                                    break;

                                case "PARSE":
                                    skip = true;
                                    iterator = (CodeMethodInvokeExpression)InternalMethods.LoopParse;
                                    break;

                                case "HKEY_LOCAL_MACHINE":
                                case "HKLM":
                                case "HKEY_USERS":
                                case "HKU":
                                case "HKEY_CURRENT_USER":
                                case "HKCU":
                                case "HKEY_CLASSES_ROOT":
                                case "HKCR":
                                case "HKEY_CURRENT_CONFIG":
                                case "HKCC":
                                    skip = true;
                                    iterator = (CodeMethodInvokeExpression)InternalMethods.LoopRegistry;
                                    break;

                                default:
                                    // TODO: file and normal loops
                                    iterator = (CodeMethodInvokeExpression)InternalMethods.Loop;
                                    break;
                            }

                            string args = parts[1];

                            if (skip)
                            {
                                int z = args.IndexOf(Multicast);
                                if (z == -1)
                                    throw new ParseException("Loop type must have an argument");
                                args = args.Substring(z);
                            }

                            foreach (string arg in SplitCommandParameters(args))
                                iterator.Parameters.Add(ParseCommandParameter(arg));
                        }
                        else
                        {
                            iterator = (CodeMethodInvokeExpression)InternalMethods.Loop;
                            iterator.Parameters.Add(new CodePrimitiveExpression(int.MaxValue));
                        }

                        string id = InternalID;

                        var init = new CodeVariableDeclarationStatement();
                        init.Name = id;
                        init.Type = new CodeTypeReference(typeof(System.Collections.IEnumerable));
                        init.InitExpression = new CodeMethodInvokeExpression(iterator, "GetEnumerator", new CodeExpression[] { });

                        var condition = new CodeMethodInvokeExpression();
                        condition.Method.TargetObject = new CodeVariableReferenceExpression(id);
                        condition.Method.MethodName = "MoveNext";

                        CodeIterationStatement loop = new CodeIterationStatement();
                        loop.InitStatement = init;
                        loop.IncrementStatement = new CodeCommentStatement(string.Empty); // for C# display
                        loop.TestExpression = condition;

                        var block = new CodeBlock(line, Scope, loop.Statements);

                        block.Type = blockOpen ? CodeBlock.BlockType.Within : CodeBlock.BlockType.Expect;
                        blocks.Push(block);
                        return loop;
                    }

                case FlowWhile:
                    {
                        bool blockOpen = false;
                        CodeExpression condition = parts.Length > 1 ? ParseFlowParameter(parts[1], true, out blockOpen) : new CodePrimitiveExpression(true);
                        CodeIterationStatement loop = new CodeIterationStatement(); //null, condition, null, statements);
                        loop.TestExpression = condition;
                        loop.InitStatement = null;
                        var block = new CodeBlock(line, Scope, loop.Statements);
                        block.Type = blockOpen ? CodeBlock.BlockType.Within : CodeBlock.BlockType.Expect;
                        blocks.Push(block);
                    }
                    break;

                case FlowBreak:
                    if (parts.Length > 1)
                        throw new ParseException(ExFlowArgNotReq);
                    return new CodeGotoStatement(); // TODO: break labels

                case FlowContinue:
                    if (parts.Length > 1)
                        throw new ParseException(ExFlowArgNotReq);
                    return new CodeGotoStatement(); // TODO: continue labels

                #endregion

                #region Return

                case FlowReturn:
                    if (Scope == mainScope)
                    {
                        if (parts.Length > 1)
                            throw new ParseException("Cannot have return parameter for entry point method");
                        return new CodeMethodReturnStatement();
                    }
                    else
                        return new CodeMethodReturnStatement(parts.Length > 1 ? ParseSingleExpression(parts[1]) : new CodePrimitiveExpression(null));

                #endregion

                default:
                    throw new ParseException(ExUnexpected);
            }

            return null;
        }

        #region Flow argument

        CodeExpression ParseFlowParameter(string code, bool inequality, out bool blockOpen)
        {
            blockOpen = false;
            code = code.Trim(Spaces);
            if (code.Length == 0)
                return null;
            else if (code.Length > 1 && code[0] == Resolve && IsSpace(code[1]))
                return ParseSingleExpression(code.Substring(2));
            else if (IsExpression(code))
            {
                int l = code.Length - 1;
                if (code.Length > 0 && code[l] == BlockOpen)
                {
                    blockOpen = true;
                    code = code.Substring(0, l);
                }
                return ParseSingleExpression(code);
            }
            else
            {
                code = StripCommentSingle(code);

                if (inequality)
                    return ParseInequality(code);

                object result;
                if (IsPrimativeObject(code, out result))
                    return new CodePrimitiveExpression(result);
                else
                    throw new ParseException(ExUnexpected);
            }
        }

        bool IsExpression(string code)
        {
            char sym = code[0];

            if (sym == ParenOpen)
                return true;

            if (!IsIdentifier(sym) && !char.IsLetterOrDigit(sym))
                return true;

            string word = code.Split(Spaces, 1)[0];
            switch (word.ToLowerInvariant())
            {
                case AndTxt:
                case OrTxt:
                case NotTxt:
                    return true;
            }

            foreach (char token in code)
            {
                if (token == ParenOpen)
                    return true;
                else if (!IsIdentifier(token))
                    return false;
            }

            return false;
        }

        CodeExpression ParseInequality(string code)
        {
            var buf = new StringBuilder(code.Length);
            int i = 0;

            while (i < code.Length && IsSpace(code[i])) i++;

            while (i < code.Length && (IsIdentifier(code[i]) || code[i] == Resolve))
                buf.Append(code[i++]);

            while (i < code.Length && IsSpace(code[i])) i++;

            if (i == code.Length)
            {
                buf.Append(Not);
                buf.Append(Equal);
                buf.Append(StringBound);
                buf.Append(StringBound);
            }
            else
            {
                char[] op = new char[] { Equal, Not, Greater, Less };
                
                if (Array.IndexOf<char>(op, code[i]) == -1)
                    throw new ParseException(ExUnexpected);

                buf.Append(code[i++]);

                if (i < code.Length && Array.IndexOf<char>(op, code[i]) != -1)
                    buf.Append(code[i++]);

                buf.Append(StringBound);

                if (i < code.Length)
                    buf.Append(code.Substring(i));

                buf.Append(StringBound);
            }

            return ParseSingleExpression(buf.ToString());
        }

        #endregion
    }
}
