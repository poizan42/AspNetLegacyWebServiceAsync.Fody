using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WeaverTests
{
    class Program
    {
        static void Main(string[] args)
        {
            string assemblyPath = @"..\..\TestWebService\bin\TestWebService.dll";
            string targetPath = Path.ChangeExtension(assemblyPath, ".2.dll");
            using (var moduleDefinition = ModuleDefinition.ReadModule(assemblyPath))
            {
                var weavingTask = new ModuleWeaver
                {
                    ModuleDefinition = moduleDefinition
                };

                weavingTask.Execute();
                moduleDefinition.Write(targetPath);
            }
        }
    }
}
