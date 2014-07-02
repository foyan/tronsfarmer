using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Tronsfarmer {

    public class Reverter {

        public XmlDocument Revert(XmlDocument xsl, XmlNamespaceManager nsmgr) {
            var res = new XmlDocument();
            RevertOne(xsl.DocumentElement, res, nsmgr);
            return res;
        }

        private static void RevertChildren(XmlElement parent, XmlNode target, XmlNamespaceManager nsmgr) {
            foreach (var elem in parent.SelectElements("*", nsmgr).Reverse()) {
                RevertOne(elem, target, nsmgr);
            }
        }

        private static void RevertOne(XmlElement source, XmlNode target, XmlNamespaceManager nsmgr) {
            
            if (source.LocalName == "stylesheet") {
                var root = (target.OwnerDocument ?? (XmlDocument)target).CreateElement("xsl", "stylesheet", "http://www.w3.org/1999/XSL/Transform");
                root.SetAttribute("version", "1.0");
                target.AppendChild(root);
                RevertChildren(source, root, nsmgr);
            }

            if (source.LocalName == "template") {

                var valueOfs = source.SelectElements(".//xsl:value-of", nsmgr);

                foreach (var vo in valueOfs) {
                    var xpath = "//text()[" + GetUpstairsXPath(vo, source, nsmgr) + "]";
                    var template = target.OwnerDocument.CreateElement("xsl", "template", "http://www.w3.org/1999/XSL/Transform");
                    template.SetAttribute("match", xpath);
                    target.AppendChild(template);

                    var elem = ParseXPath(source.GetAttribute("match"), target.OwnerDocument);
                    if (elem.Item1 != null) {
                        template.AppendChild(elem.Item1);
                        foreach (var x in elem.Item2) {
                            var apt = target.OwnerDocument.CreateElement("xsl", "value-of", "http://www.w3.org/1999/XSL/Transform");
                            apt.SetAttribute("select", ".");
                            x.AppendChild(apt);
                        }
                    }
                }

                if (false) {

                    var applyTemplates = source.SelectElements(".//xsl:apply-templates", nsmgr);

                    foreach (var at in applyTemplates) {
                        var xpath = "//text()[" + GetUpstairsXPath(at, source, nsmgr) + "]";
                        var template = target.OwnerDocument.CreateElement("xsl", "template", "http://www.w3.org/1999/XSL/Transform");
                        template.SetAttribute("match", xpath);
                        target.AppendChild(template);

                        var elem = ParseXPath(source.GetAttribute("match"), target.OwnerDocument);
                        if (elem.Item1 != null) {
                            template.AppendChild(elem.Item1);
                            foreach (var x in elem.Item2) {
                                var apt = target.OwnerDocument.CreateElement("xsl", "value-of", "http://www.w3.org/1999/XSL/Transform");
                                apt.SetAttribute("select", ".");
                                x.AppendChild(apt);
                            }
                        }
                    }

                }

            }

        }

        private static Tuple<XmlElement, List<XmlElement>> ParseXPath(string xpath, XmlDocument owner) {
            var parser = new XPathParser<XElement>();
            var xe = parser.Parse(xpath, new XPathTreeBuilder());

            return GetElementFromPath(xe, owner);
        }

        private static Tuple<XmlElement, List<XmlElement>> GetElementFromPath(XElement path, XmlDocument owner) {
            if (path.Name == "Child" && path.Attribute("nodeType").Value == "Element") {
                var elem = owner.CreateElement(path.Attribute("name").Value);
                var children = new List<XmlElement>();
                if (!path.Elements().Any()) {
                    children.Add(elem);
                }
                foreach (var ch in path.Elements()) {
                    var che = GetElementFromPath(ch, owner);
                    if (che != null) {
                        elem.AppendChild(che.Item1);
                        children.AddRange(che.Item2);
                    }
                }

                return Tuple.Create(elem, children);
            }

            if (path.Name == "Root") {
                var elem = owner.CreateElement("any-given-root");
                var children = new List<XmlElement>();
                if (!path.Elements().Any()) {
                    children.Add(elem);
                }
                foreach (var ch in path.Elements()) {
                    var che = GetElementFromPath(ch, owner);
                    if (che != null) {
                        elem.AppendChild(che.Item1);
                        children.AddRange(che.Item2);
                    }
                }

                return Tuple.Create(elem, children);
            }

            return null;
        }

        private static string GetUpstairsXPath(XmlElement elem, XmlElement root, XmlNamespaceManager nsmgr) {
            if (elem == root) {
                return "";
            }
            if (elem.NamespaceURI == "http://www.w3.org/1999/XSL/Transform") {
                return GetUpstairsXPath((XmlElement) elem.ParentNode, root, nsmgr);
            }

            var xpath = "parent::node()[name() = '" + elem.LocalName + "'";

            // attributes
            xpath += elem.Attributes.OfType<XmlAttribute>().Aggregate("", (current, attr) => current + (" and @" + attr.LocalName + "='" + attr.Value + "'"));

            // preceding siblings
            foreach (var x in elem.SelectNodes("./preceding-sibling::*").OfType<XmlNode>().Union(elem.SelectNodes("./preceding-sibling::text()").OfType<XmlNode>()).Reverse()) {
                xpath += " and preceding-sibling::" + (x.NodeType == XmlNodeType.Text ? "text()" : x.LocalName);
                if (x.NodeType == XmlNodeType.Text) {
                    xpath += "[contains(., '" + x.Value.Trim() + "')]";
                } else if (x.Attributes.Count > 0) {
                    xpath += "[" + string.Join(" and ", x.Attributes.OfType<XmlAttribute>().Select(a => a.LocalName + "='" + a.Value + "'")) + "]";
                }
            }

            // following siblings
            foreach (var x in elem.SelectNodes("./following-sibling::*").OfType<XmlNode>().Union(elem.SelectNodes("./following-sibling::text()").OfType<XmlNode>())) {
                xpath += " and following-sibling::" + (x.NodeType == XmlNodeType.Text ? "text()" : x.LocalName);
                if (x.NodeType == XmlNodeType.Text) {
                    xpath += "[contains(., '" + x.Value.Trim() + "')]";
                } else if (x.Attributes.Count > 0) {
                    xpath += "[" + string.Join(" and ", x.Attributes.OfType<XmlAttribute>().Select(a => a.LocalName + "='" + a.Value + "'")) + "]";
                }
            }

            var up = GetUpstairsXPath((XmlElement) elem.ParentNode, root, nsmgr);
            if (up != "") {
                xpath += " and " + up;
            }

            xpath += "]";

            return xpath;
        }

    }

}
