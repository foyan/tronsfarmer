using System.IO;

namespace Tronsfarmer {

    class Program {

        static void Main(string[] args) {

            var reader = new Reader();

            reader.Read("Simple.xslt");

            var reverter = new Reverter();

            var reversed = reverter.Revert(reader.Document, reader.NamespaceManager);

            reversed.Save(@"c:\temp\reversed.xslt");

            var result = new Transformer().Transform(reversed.OuterXml, File.ReadAllText("Simple_Result_Origin.xml"));

            File.WriteAllText(@"c:\temp\result.xml", result);

        }

    }

}
