using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Web;
using System.Web.Services;

namespace TestWebService
{
    /// <summary>
    /// Summary description for TestService
    /// </summary>
    [WebService(Namespace = "http://tempuri.org/")]
    [WebServiceBinding(ConformsTo = WsiProfiles.BasicProfile1_1)]
    [System.ComponentModel.ToolboxItem(false)]
    // To allow this Web Service to be called from script, using ASP.NET AJAX, uncomment the following line. 
    // [System.Web.Script.Services.ScriptService]
    public class TestService : System.Web.Services.WebService
    {

        [WebMethod]
        public string HelloWorld()
        {
            return "Hello World";
        }

        [WebMethod]
        public async Task<string> DelayedHelloWorld(int ms)
        {
            Debug.WriteLine("Before delay");
            await Task.Delay(ms);
            Debug.WriteLine("After delay");
            return "Hello, World!";
        }

        [WebMethod]
        public async Task Delay(int ms)
        {
            Debug.WriteLine("Before delay");
            await Task.Delay(ms);
            Debug.WriteLine("After delay");
        }
    }
}
