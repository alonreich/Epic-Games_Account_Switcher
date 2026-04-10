using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Epic_Games_Account_Switcher_Globals
{
    public partial class Globals
    {

    }

    public static class JsonExtensions
    {

        public static JToken ReplacePath<T>(this JToken root, string path, T newValue)
        {
            if (root == null || path == null)
                throw new ArgumentNullException();

            foreach (var value in root.SelectTokens(path).ToList())
            {
                if (value == root)
                    root = JToken.FromObject(newValue);
                else
                    value.Replace(JToken.FromObject(newValue));
            }

            return root;
        }

        public static string ReplacePath<T>(string jsonString, string path, T newValue)
        {
            return JToken.Parse(jsonString).ReplacePath(path, newValue).ToString();
        }
    }
}

