using System.Xml;

namespace Tronsfarmer {

    public class Reader {

        public void Read(string filename) {
            Document = new XmlDocument();
            NamespaceManager = new XmlNamespaceManager(Document.NameTable);
            NamespaceManager.AddNamespace("xsl", "http://www.w3.org/1999/XSL/Transform");

            Document.Load(filename);
        }

        public XmlNamespaceManager NamespaceManager { get; private set; }

        public XmlDocument Document { get; private set; }

    }

}
