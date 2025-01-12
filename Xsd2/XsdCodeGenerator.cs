﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Schema;
using System.Xml.Serialization;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.ComponentModel;
using Microsoft.CSharp;
using System.IO;
using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.VisualBasic;
using Xsd2.Capitalizers;

namespace Xsd2
{
    public class XsdCodeGenerator
    {
        public XsdCodeGeneratorOptions Options { get; set; }
        public Action<CodeNamespace, XmlSchema> OnValidateGeneratedCode { get; set; }
        private const char DocMapSeparator = '.';

        XmlSchemas xsds = new XmlSchemas();
        HashSet<XmlSchema> importedSchemas = new HashSet<XmlSchema>();

        public void Generate(IList<string> schemas, TextWriter output)
        {
            if (Options == null)
            {
                Options = new XsdCodeGeneratorOptions
                {
                    UseLists = true,
                    PropertyNameCapitalizer = new FirstCharacterCapitalizer(),
                    OutputNamespace = "Xsd2",
                    UseNullableTypes = true,
                    AttributesToRemove =
                    {
                        "System.Diagnostics.DebuggerStepThroughAttribute"
                    }
                };
            }

            if (Options.Imports != null)
            {
                foreach (var import in Options.Imports)
                {
                    if (File.Exists(import))
                    {
                        ImportImportedSchema(import);
                    }
                    else if (Directory.Exists(import))
                    {
                        foreach (var file in Directory.GetFiles(import, "*.xsd"))
                            ImportImportedSchema(file);
                    }
                    else
                    {
                        throw new InvalidOperationException(string.Format("Import '{0}' is not a file nor a directory.", import));
                    }
                }
            }

            var inputs = new List<(XmlSchema schema, XElement xml)>();

            foreach (var path in schemas)
            {
                string content = File.ReadAllText(path);
                using (var r = new MemoryStream(Encoding.UTF8.GetBytes(content)))
                {
                    XmlSchema xsd = XmlSchema.Read(r, null);
                    XElement rootElement = XElement.Parse(content);
                    LoadIncludes(rootElement, path, inputs);
                    inputs.Add((xsd, rootElement));
                }
            }

            foreach ((XmlSchema schema, XElement xml) input in inputs)
            {
                xsds.Add(input.schema);
            }

            xsds.Compile(null, true);

            XmlSchemaImporter schemaImporter = new XmlSchemaImporter(xsds);


            // create the codedom
            CodeNamespace codeNamespace = new CodeNamespace(Options.OutputNamespace);
            XmlCodeExporter codeExporter = new XmlCodeExporter(codeNamespace);

            List<XmlTypeMapping> maps = new List<XmlTypeMapping>();
            foreach (var input in inputs)
                foreach (XmlSchemaElement schemaElement in input.schema.Elements.Values)
                {
                    if (!ElementBelongsToImportedSchema(schemaElement))
                        maps.Add(schemaImporter.ImportTypeMapping(schemaElement.QualifiedName));
                }


            foreach (var input in inputs)
                foreach (XmlSchemaComplexType schemaElement in input.schema.Items.OfType<XmlSchemaComplexType>())
                {
                    maps.Add(schemaImporter.ImportSchemaType(schemaElement.QualifiedName));
                }

            foreach (var input in inputs)
                foreach (XmlSchemaSimpleType schemaElement in input.schema.Items.OfType<XmlSchemaSimpleType>())
                {
                    maps.Add(schemaImporter.ImportSchemaType(schemaElement.QualifiedName));
                }

            foreach (XmlTypeMapping map in maps)
            {
                codeExporter.ExportTypeMapping(map);
            }

            foreach (var xsd in inputs)
                ImproveCodeDom(codeNamespace, xsd.schema);

            if (OnValidateGeneratedCode != null)
                foreach (var xsd in inputs)
                    OnValidateGeneratedCode(codeNamespace, xsd.schema);

            //add the documentation sections as code doc
            foreach (var xsd in inputs)
            {
                AddDoc(codeNamespace, xsd.xml);
            }

            // Check for invalid characters in identifiers
            CodeGenerator.ValidateIdentifiers(codeNamespace);

            if (Options.WriteFileHeader)
            {
                // output the header
                string lineCommentCharacter;
                switch (Options.Language)
                {
                    case XsdCodeGeneratorOutputLanguage.VB:
                        lineCommentCharacter = "'";
                        break;
                    default:
                        lineCommentCharacter = "//";
                        break;
                }

                output.WriteLine("{0}------------------------------------------------------------------------------", lineCommentCharacter);
                output.WriteLine("{0} <auto-generated>", lineCommentCharacter);
                output.WriteLine("{0}     This code has been generated by a tool.", lineCommentCharacter);
                output.WriteLine("{0} </auto-generated>", lineCommentCharacter);
                output.WriteLine("{0}------------------------------------------------------------------------------", lineCommentCharacter);
                output.WriteLine();
            }

            // output the C# code
            CodeDomProvider codeProvider;
            switch (Options.Language)
            {
                case XsdCodeGeneratorOutputLanguage.VB:
                    codeProvider = new VBCodeProvider();
                    break;
                default:
                    codeProvider = new CSharpCodeProvider();
                    break;
            }

            var codeGeneratorOptions = new CodeGeneratorOptions()
            {
                BracingStyle = "C",
            };
            codeProvider.GenerateCodeFromNamespace(codeNamespace, output, codeGeneratorOptions);
        }

        private void LoadIncludes(XElement content, string path, List<(XmlSchema schema, XElement xml)> inputs)
        {
            XName includeElementName = XName.Get("include", content.Name.Namespace.NamespaceName);
            string basePath = Path.GetDirectoryName(path);
            foreach (XElement includeElement in content.Elements(includeElementName))
            {
                string includePath = includeElement.Attribute("schemaLocation")?.Value;
                if (includePath != null)
                {
                    string includeContent = File.ReadAllText(Path.Combine(basePath, includePath));
                    using (var r = new MemoryStream(Encoding.UTF8.GetBytes(includeContent)))
                    {
                        XmlSchema xsd = XmlSchema.Read(r, null);
                        XElement rootElement = XElement.Parse(includeContent);
                        LoadIncludes(rootElement, path, inputs);
                        inputs.Add((xsd, rootElement));
                    }
                }
            }
        }

        private Dictionary<string, string> CreateDocMap(XElement root)
        {
            Dictionary<string, string> docMap = new Dictionary<string, string>();
            AppendDocMap(root, ref docMap, "");
            return docMap;
        }

        private void ReplaceCommentsDoc(CodeCommentStatementCollection comments, string doc)
        {
            comments.Clear();
            comments.Add(new CodeCommentStatement("<summary>", true));
            comments.Add(new CodeCommentStatement(doc, true));
            comments.Add(new CodeCommentStatement("</summary>", true));
        }

        private void AppendDocMap(XElement element, ref Dictionary<string, string> docMap, string path)
        {
            string nextPath = path;
            string nextStep = path;
            string namespaceName = element.Name.Namespace.NamespaceName;
            XName annotationName = XName.Get("annotation", namespaceName);
            XName documentationName = XName.Get("documentation", namespaceName);
            XElement annotationElement = element.Element(annotationName);
            XElement documentationElement = annotationElement?.Element(documentationName);
            string itemName = element.Attribute("name")?.Value;

            if (itemName != null)
            {
                nextStep = $"{nextStep}{itemName}";
            }

            if (documentationElement != null)
            {
                if (itemName == null)
                {
                    throw new ApplicationException($"Expected to find Name attribute on {element}");
                }

                nextPath = string.Join(DocMapSeparator.ToString(), nextPath, itemName).Trim(DocMapSeparator);

                docMap.Add(nextPath, documentationElement.Value);
            }

            foreach (XElement child in element.Elements())
            {
                AppendDocMap(child, ref docMap, nextStep);
            }
        }

        private void AddDoc(CodeNamespace space, XElement xml)
        {
            Dictionary<string, string> docMap = CreateDocMap(xml);

            Debug.WriteLine($"Handling space {space.Name}");
            foreach (CodeTypeDeclaration type in space.Types)
            {
                Debug.WriteLine($"Adding comment to type {type.Name}");
                if (docMap.TryGetValue(type.Name, out string doc))
                {
                    ReplaceCommentsDoc(type.Comments, doc);
                }
                AddDocToChildren(type.Members, docMap, type.Name);
            }
        }

        private void AddDocToChildren(CodeTypeMemberCollection collection, Dictionary<string, string> docMap, string parentPath)
        {
            foreach (CodeTypeMember codeTypeMember in collection)
            {
                string path = $"{parentPath}.{codeTypeMember.Name}".Trim(DocMapSeparator);
                Debug.WriteLine($"Adding comment to type {codeTypeMember.Name}");
                if (docMap.TryGetValue(path, out string doc))
                {
                    ReplaceCommentsDoc(codeTypeMember.Comments, doc);
                }
            }
        }

        private void ImportImportedSchema(string schemaFilePath)
        {
            using (var s = File.OpenRead(schemaFilePath))
            {
                var importedSchema = XmlSchema.Read(s, null);
                xsds.Add(importedSchema);
                importedSchemas.Add(importedSchema);
            }
        }

        private bool ElementBelongsToImportedSchema(XmlSchemaElement element)
        {
            var node = element.Parent;
            while (node != null)
            {
                if (node is XmlSchema)
                {
                    var schema = (XmlSchema)node;
                    return importedSchemas.Contains(schema);
                }
                else
                    node = node.Parent;
            }
            return false;
        }

        /// <summary>
        /// Shamelessly taken from Xsd2Code project
        /// </summary>
        private bool ContainsTypeName(XmlSchema schema, CodeTypeDeclaration type)
        {
            //TODO: Does not work for combined anonymous types
            //fallback: Check if the namespace attribute of the type equals the namespace of the file.
            //first, find the XmlType attribute.
            var ns = ExtractNamespace(type);
            if (ns != null && ns != schema.TargetNamespace)
                return false;

            if (!Options.ExcludeImportedTypesByNameAndNamespace)
                return true;

            foreach (var item in schema.Items)
            {
                var complexItem = item as XmlSchemaComplexType;
                if (complexItem != null)
                {
                    if (complexItem.Name == type.Name)
                    {
                        return true;
                    }
                }

                var simpleItem = item as XmlSchemaSimpleType;
                if (simpleItem != null)
                {
                    if (simpleItem.Name == type.Name)
                    {
                        return true;
                    }
                }


                var elementItem = item as XmlSchemaElement;
                if (elementItem != null)
                {
                    if (elementItem.Name == type.Name)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private String ExtractNamespace(CodeTypeDeclaration type)
        {
            foreach (CodeAttributeDeclaration attribute in type.CustomAttributes)
            {
                if (attribute.Name == "System.Xml.Serialization.XmlTypeAttribute")
                {
                    foreach (CodeAttributeArgument argument in attribute.Arguments)
                    {
                        if (argument.Name == "Namespace")
                        {
                            return (string)((CodePrimitiveExpression)argument.Value).Value;
                        }
                    }
                }
            }

            return null;
        }

        private void ImproveCodeDom(CodeNamespace codeNamespace, XmlSchema schema)
        {
            var nonElementAttributes = new HashSet<string>(new[]
            {
                "System.Xml.Serialization.XmlAttributeAttribute",
                "System.Xml.Serialization.XmlIgnoreAttribute",
                "System.Xml.Serialization.XmlTextAttribute",
            });

            var nullValue = new CodePrimitiveExpression();

            codeNamespace.Imports.Add(new CodeNamespaceImport("System"));
            codeNamespace.Imports.Add(new CodeNamespaceImport("System.Collections.Generic"));

            if (Options.UsingNamespaces != null)
                foreach (var ns in Options.UsingNamespaces)
                    codeNamespace.Imports.Add(new CodeNamespaceImport(ns));

            var neverBrowsableAttribute = new CodeAttributeDeclaration("System.ComponentModel.EditorBrowsable",
                new CodeAttributeArgument(new CodeFieldReferenceExpression(new CodeTypeReferenceExpression("System.ComponentModel.EditorBrowsableState"), "Never")));

            var removedTypes = new List<CodeTypeDeclaration>();

            var changedTypeNames = new Dictionary<string, string>();
            var newTypeNames = new HashSet<string>();

            if (Options.UseXLinq)
            {
                changedTypeNames.Add("System.Xml.XmlNode", "System.Xml.Linq.XNode");
                changedTypeNames.Add("System.Xml.XmlElement", "System.Xml.Linq.XElement");
                changedTypeNames.Add("System.Xml.XmlAttribute", "System.Xml.Linq.XAttribute");
            }

            foreach (CodeTypeDeclaration codeType in codeNamespace.Types)
            {
                if (Options.ExcludeImportedTypes && Options.Imports != null && Options.Imports.Count > 0)
                    if (!ContainsTypeName(schema, codeType))
                    {
                        removedTypes.Add(codeType);
                        continue;
                    }

                var attributesToRemove = new HashSet<CodeAttributeDeclaration>();
                foreach (CodeAttributeDeclaration att in codeType.CustomAttributes)
                {
                    if (Options.AttributesToRemove.Contains(att.Name))
                    {
                        attributesToRemove.Add(att);
                    }
                    else
                    {
                        switch (att.Name)
                        {
                            case "System.Xml.Serialization.XmlRootAttribute":
                                var nullableArgument = att.Arguments.Cast<CodeAttributeArgument>().FirstOrDefault(x => x.Name == "IsNullable");
                                if (nullableArgument != null && (bool) ((CodePrimitiveExpression) nullableArgument.Value).Value)
                                {
                                    // Remove nullable root attribute
                                    attributesToRemove.Add(att);
                                }
                                break;
                        }
                    }
                }

                foreach (var att in attributesToRemove)
                {
                    codeType.CustomAttributes.Remove(att);
                }

                if (Options.TypeNameCapitalizer != null)
                {
                    var newName = Options.TypeNameCapitalizer.Capitalize(codeNamespace, codeType);
                    if (newName != codeType.Name)
                    {
                        SetAttributeOriginalName(codeType, codeType.GetOriginalName(), "System.Xml.Serialization.XmlTypeAttribute");
                        var newNameToAdd = newName;
                        var index = 0;
                        while (!newTypeNames.Add(newNameToAdd))
                        {
                            index += 1;
                            newNameToAdd = string.Format("{0}{1}", newName, index);
                        }
                        changedTypeNames.Add(codeType.Name, newNameToAdd);
                        codeType.Name = newNameToAdd;
                    }
                }

                var members = new Dictionary<string, CodeTypeMember>();
                foreach (CodeTypeMember member in codeType.Members)
                    members[member.Name] = member;

                if (Options.EnableDataBinding && codeType.IsClass && codeType.BaseTypes.Count == 0)
                {
                    codeType.BaseTypes.Add(typeof(object));
                    codeType.BaseTypes.Add(typeof(INotifyPropertyChanged));

                    codeType.Members.Add(new CodeMemberEvent()
                    {
                        Name = "PropertyChanged",
                        ImplementationTypes = { typeof(INotifyPropertyChanged) },
                        Attributes = MemberAttributes.Public,
                        Type = new CodeTypeReference(typeof(PropertyChangedEventHandler))
                    });

                    codeType.Members.Add(new CodeMemberMethod()
                    {
                        Name = "RaisePropertyChanged",
                        Attributes = MemberAttributes.Family | MemberAttributes.Final,
                        Parameters =
                        {
                            new CodeParameterDeclarationExpression(typeof(string), "propertyName")
                        },
                        Statements =
                        {
                            new CodeVariableDeclarationStatement(typeof(PropertyChangedEventHandler), "propertyChanged",
                                new CodeEventReferenceExpression(new CodeThisReferenceExpression(), "PropertyChanged")),
                            new CodeConditionStatement(new CodeBinaryOperatorExpression(new CodeVariableReferenceExpression("propertyChanged"), CodeBinaryOperatorType.IdentityInequality, nullValue),
                                new CodeExpressionStatement(new CodeDelegateInvokeExpression(new CodeVariableReferenceExpression("propertyChanged"),
                                    new CodeThisReferenceExpression(),
                                    new CodeObjectCreateExpression(typeof(PropertyChangedEventArgs), new CodeArgumentReferenceExpression("propertyName")))))
                        }
                    });
                }

                bool mixedContentDetected = Options.MixedContent && members.ContainsKey("textField") && members.ContainsKey("itemsField");

                var orderIndex = 0;
                foreach (CodeTypeMember member in members.Values)
                {
                    if (member is CodeMemberField)
                    {
                        CodeMemberField field = (CodeMemberField)member;

                        if (mixedContentDetected)
                        {
                            switch (field.Name)
                            {
                                case "textField":
                                    codeType.Members.Remove(member);
                                    continue;
                                case "itemsField":
                                    field.Type = new CodeTypeReference(typeof(object[]));
                                    break;
                            }
                        }

                        if (Options.UseLists && field.Type.ArrayRank > 0)
                        {
                            CodeTypeReference type = new CodeTypeReference(typeof(List<>))
                            {
                                TypeArguments =
                                {
                                    field.Type.ArrayElementType
                                }
                            };

                            field.Type = type;
                        }

                        if (codeType.IsEnum && Options.EnumValueCapitalizer != null)
                        {
                            var newName = Options.EnumValueCapitalizer.Capitalize(codeNamespace, member);
                            if (newName != member.Name)
                            {
                                SetAttributeOriginalName(member, member.GetOriginalName(), "System.Xml.Serialization.XmlEnumAttribute");
                                member.Name = newName;
                            }
                        }
                    }

                    if (member is CodeMemberProperty)
                    {
                        CodeMemberProperty property = (CodeMemberProperty)member;

                        // Is this "*Specified" property part of a "propertyName" and "propertyNameSpecified" combination?
                        var isSpecifiedProperty = property.Name.EndsWith("Specified") && members.ContainsKey(property.Name.Substring(0, property.Name.Length - 9));

                        if (mixedContentDetected)
                        {
                            switch (property.Name)
                            {
                                case "Text":
                                    codeType.Members.Remove(member);
                                    continue;
                                case "Items":
                                    property.Type = new CodeTypeReference(typeof(object[]));
                                    property.CustomAttributes.Add(new CodeAttributeDeclaration("System.Xml.Serialization.XmlTextAttribute", new CodeAttributeArgument { Name = "", Value = new CodeTypeOfExpression(new CodeTypeReference(typeof(string))) }));
                                    break;
                            }
                        }

                        if (Options.UseLists && property.Type.ArrayRank > 0)
                        {
                            CodeTypeReference type = new CodeTypeReference(typeof(List<>))
                            {
                                TypeArguments =
                                {
                                    property.Type.ArrayElementType
                                }
                            };

                            property.Type = type;
                        }

                        bool capitalizeProperty;
                        if (!isSpecifiedProperty)
                        {
                            if (Options.UseNullableTypes)
                            {
                                var fieldName = GetFieldName(property.Name, "Field");
                                CodeTypeMember specified;
                                if (members.TryGetValue(property.Name + "Specified", out specified))
                                {
                                    var nullableProperty = new CodeMemberProperty
                                    {
                                        Name = property.Name,
                                        Type = new CodeTypeReference(typeof(Nullable<>)) { TypeArguments = { property.Type.BaseType } },
                                        HasGet = true,
                                        HasSet = true,
                                        Attributes = MemberAttributes.Public | MemberAttributes.Final
                                    };

                                    nullableProperty.GetStatements.Add(
                                        new CodeConditionStatement(new CodeVariableReferenceExpression(fieldName + "Specified"),
                                            new CodeStatement[] { new CodeMethodReturnStatement(new CodeVariableReferenceExpression(fieldName)) },
                                            new CodeStatement[] { new CodeMethodReturnStatement(new CodePrimitiveExpression()) }
                                        ));

                                    nullableProperty.SetStatements.Add(
                                        new CodeConditionStatement(new CodeBinaryOperatorExpression(new CodePropertySetValueReferenceExpression(), CodeBinaryOperatorType.IdentityInequality, nullValue),
                                            new CodeStatement[]
                                            {
                                                new CodeAssignStatement(new CodeVariableReferenceExpression(fieldName + "Specified"),
                                                    new CodePrimitiveExpression(true)),
                                                new CodeAssignStatement(new CodeVariableReferenceExpression(fieldName),
                                                    new CodePropertyReferenceExpression(new CodePropertySetValueReferenceExpression(), "Value")),
                                            },
                                            new CodeStatement[]
                                            {
                                                new CodeAssignStatement(
                                                    new CodeVariableReferenceExpression(fieldName + "Specified"),
                                                    new CodePrimitiveExpression(false)),
                                            }
                                        ));

                                    nullableProperty.CustomAttributes.Add(new CodeAttributeDeclaration
                                    {
                                        Name = "System.Xml.Serialization.XmlIgnoreAttribute"
                                    });

                                    codeType.Members.Add(nullableProperty);

                                    foreach (CodeAttributeDeclaration attribute in property.CustomAttributes)
                                        if (attribute.Name == "System.Xml.Serialization.XmlAttributeAttribute")
                                            attribute.Arguments.Add(new CodeAttributeArgument
                                            {
                                                Name = "AttributeName",
                                                Value = new CodePrimitiveExpression(property.Name)
                                            });

                                    property.Name = "_" + property.Name;
                                    specified.Name = "_" + specified.Name;

                                    if (Options.HideUnderlyingNullableProperties)
                                    {
                                        property.CustomAttributes.Add(neverBrowsableAttribute);
                                        specified.CustomAttributes.Add(neverBrowsableAttribute);
                                    }

                                    var elementAttribute = property
                                        .CustomAttributes.Cast<CodeAttributeDeclaration>()
                                        .Where(x => x.Name == "System.Xml.Serialization.XmlElementAttribute")
                                        .SingleOrDefault() ?? new CodeAttributeDeclaration("System.Xml.Serialization.XmlElementAttribute");

                                    elementAttribute.Arguments.Add(new CodeAttributeArgument("ElementName", new CodePrimitiveExpression(property.Name.Replace("_", ""))));

                                    property = nullableProperty;
                                }
                            }

                            if (Options.PreserveOrder)
                            {
                                if (!property.CustomAttributes.Cast<CodeAttributeDeclaration>().Any(x => nonElementAttributes.Contains(x.Name)))
                                {
                                    var elementAttributes = property
                                        .CustomAttributes.Cast<CodeAttributeDeclaration>()
                                        .Where(x => x.Name == "System.Xml.Serialization.XmlElementAttribute")
                                        .ToList();
                                    if (elementAttributes.Count == 0)
                                    {
                                        var elementAttribute = new CodeAttributeDeclaration("System.Xml.Serialization.XmlElementAttribute");
                                        property.CustomAttributes.Add(elementAttribute);
                                        elementAttributes.Add(elementAttribute);
                                    }

                                    foreach (var elementAttribute in elementAttributes)
                                    {
                                        elementAttribute.Arguments.Add(new CodeAttributeArgument("Order", new CodePrimitiveExpression(orderIndex)));
                                    }

                                    orderIndex += 1;
                                }
                            }

                            if (Options.EnableDataBinding)
                            {
                                property.SetStatements.Add(new CodeMethodInvokeExpression(new CodeThisReferenceExpression(), "RaisePropertyChanged", new CodePrimitiveExpression(property.Name)));
                            }

                            capitalizeProperty = Options.PropertyNameCapitalizer != null;
                        }
                        else if (!Options.UseNullableTypes)
                        {
                            if (Options.EnableDataBinding)
                            {
                                property.SetStatements.Add(new CodeMethodInvokeExpression(new CodeThisReferenceExpression(), "RaisePropertyChanged", new CodePrimitiveExpression(property.Name)));
                            }

                            capitalizeProperty = Options.PropertyNameCapitalizer != null;
                        }
                        else
                        {
                            capitalizeProperty = false;
                        }

                        if (capitalizeProperty)
                        {
                            var newName = Options.PropertyNameCapitalizer.Capitalize(codeNamespace, property);
                            if (newName != property.Name)
                            {
                                SetAttributeOriginalName(property, property.GetOriginalName(), "System.Xml.Serialization.XmlElementAttribute");
                                property.Name = newName;
                            }
                        }
                    }
                }
            }

            // Remove types
            foreach (var rt in removedTypes)
                codeNamespace.Types.Remove(rt);

            // Fixup changed type names
            if (changedTypeNames.Count != 0)
            {
                foreach (CodeTypeDeclaration codeType in codeNamespace.Types)
                {
                    if (codeType.IsEnum)
                        continue;

                    FixAttributeTypeReference(changedTypeNames, codeType);

                    foreach (CodeTypeMember member in codeType.Members)
                    {
                        var memberField = member as CodeMemberField;
                        if (memberField != null)
                        {
                            FixTypeReference(changedTypeNames, memberField.Type);
                            FixAttributeTypeReference(changedTypeNames, memberField);
                        }

                        var memberProperty = member as CodeMemberProperty;
                        if (memberProperty != null)
                        {
                            FixTypeReference(changedTypeNames, memberProperty.Type);
                            FixAttributeTypeReference(changedTypeNames, memberProperty);
                        }
                    }
                }
            }
        }

        private static void FixAttributeTypeReference(IReadOnlyDictionary<string, string> changedTypeNames, CodeTypeMember member)
        {
            foreach (CodeAttributeDeclaration attribute in member.CustomAttributes)
            {
                foreach (CodeAttributeArgument argument in attribute.Arguments)
                {
                    var typeOfExpr = argument.Value as CodeTypeOfExpression;
                    if (typeOfExpr != null)
                    {
                        FixTypeReference(changedTypeNames, typeOfExpr.Type);
                    }
                }
            }
        }

        private static void FixTypeReference(IReadOnlyDictionary<string, string> changedTypeNames, CodeTypeReference typeReference)
        {
            string newTypeName;
            if (!string.IsNullOrEmpty(typeReference.BaseType) && changedTypeNames.TryGetValue(typeReference.BaseType, out newTypeName))
            {
                typeReference.BaseType = newTypeName;
            }

            if (typeReference.ArrayElementType != null && changedTypeNames.TryGetValue(typeReference.ArrayElementType.BaseType, out newTypeName))
            {
                typeReference.ArrayElementType.BaseType = newTypeName;
            }
        }

        private static void SetAttributeOriginalName(CodeTypeMember member, string originalName, string newAttributeType)
        {
            var elementIgnored = false;
            var attributesThatNeedName = new List<CodeAttributeDeclaration>();
            foreach (CodeAttributeDeclaration attribute in member.CustomAttributes)
            {
                switch (attribute.Name)
                {
                    case "System.Xml.Serialization.XmlIgnoreAttribute":
                        elementIgnored = true;
                        break;
                    case "System.Xml.Serialization.XmlAttributeAttribute":
                    case "System.Xml.Serialization.XmlElementAttribute":
                    case "System.Xml.Serialization.XmlArrayItemAttribute":
                    case "System.Xml.Serialization.XmlEnumAttribute":
                    case "System.Xml.Serialization.XmlTypeAttribute":
                    case "System.Xml.Serialization.XmlRootAttribute":
                        attributesThatNeedName.Add(attribute);
                        break;
                }
            }

            if (elementIgnored)
                return;

            if (attributesThatNeedName.Count == 0)
            {
                var attribute = new CodeAttributeDeclaration(newAttributeType);
                attributesThatNeedName.Add(attribute);
                member.CustomAttributes.Add(attribute);
            }

            var nameArgument = new CodeAttributeArgument { Name = "", Value = new CodePrimitiveExpression(originalName) };

            foreach (var attribute in attributesThatNeedName)
            {
                switch (attribute.Name)
                {
                    case "System.Xml.Serialization.XmlTypeAttribute":
                        if (attribute.IsAnonymousTypeArgument())
                            continue;
                        break;
                }

                var hasNameAttribute = attribute.Arguments.Cast<CodeAttributeArgument>().Any(x => x.IsNameArgument());
                if (!hasNameAttribute)
                    attribute.Arguments.Insert(0, nameArgument);
            }
        }

        private static string GetFieldName(string p, string suffix = null)
        {
            return p.Substring(0, 1).ToLower() + p.Substring(1) + suffix;
        }
    }
}
