using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Xsl;

namespace Tronsfarmer {

    public class Transformer {

        public string Transform(string xsl, string doc) {
            var transform = new XslCompiledTransform();
            transform.Load(new XmlTextReader(new StringReader(xsl)));
            var sb = new StringBuilder();
            transform.Transform(new XmlTextReader(new StringReader(doc)), new XmlTextWriter(new StringWriter(sb)));

            return sb.ToString();
        }

    }

}
