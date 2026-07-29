using System;
using System.Collections;

namespace LegacyApp
{
    class InventoryManager
    {
        public void BuildReport()
        {
            // Anti-pattern: Non-generic collections (require boxing/casting, no type safety)
            ArrayList items = new ArrayList();
            items.Add("Widget");
            items.Add("Gadget");
            items.Add("Gizmo");

            Hashtable prices = new Hashtable();
            prices["Widget"] = 9.99;
            prices["Gadget"] = 14.99;
            prices["Gizmo"] = 4.99;

            // Anti-pattern: String concatenation inside a loop (O(n^2) allocations)
            string report = "";
            foreach (string item in items)
            {
                report += item + ": $" + prices[item] + "\n";
            }

            Console.WriteLine(report);
        }

        public void SafeMethod()
        {
            Console.WriteLine("Nothing to see here.");
        }
    }

    class Program
    {
        static void Main()
        {
            var manager = new InventoryManager();
            manager.BuildReport();
            manager.SafeMethod();
        }
    }
}
