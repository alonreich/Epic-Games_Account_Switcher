using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Epic_Games_Account_Switcher_Globals
{
    public partial class Globals
    {
        public static bool ReadWebUrl(string requestUri, out string webText, string cookies = "")
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("user-agent", "Epic-Games_Account_Switcher");
                if (!string.IsNullOrEmpty(cookies)) client.DefaultRequestHeaders.Add("cookie", cookies);

                var response = client.GetAsync(requestUri).Result;
                webText = response.Content.ReadAsStringAsync().Result;
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                WriteToLog($"ERROR LOADING URL: {requestUri}", ex);
                webText = "";
                return false;
            }
        }
    }
}
