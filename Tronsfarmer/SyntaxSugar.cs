using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace Tronsfarmer {

    public static class SyntaxSugar {

        public static IEnumerable<XmlElement> SelectElements(this XmlElement elem, string xpath, XmlNamespaceManager nsmgr) {
            return elem.SelectNodes(xpath, nsmgr).OfType<XmlElement>().ToList();
        } 

    }

}
