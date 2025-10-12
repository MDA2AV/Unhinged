// See https://aka.ms/new-console-template for more information

internal class Program
{
    public static void Main(string[] args)
    {
        var a = new Person() { Name = "Alice" };
        var b = new Person() { Name = "Alice" };
        
        ref Person? person = ref a;
        Console.WriteLine(person.Name);
        person.Name = "Anton";
        Console.WriteLine(a.Name);

        person = b;
        Console.WriteLine(person.Name);
        person.Name = "Anton";
        Console.WriteLine(b.Name);
        
        ref var name = ref a.Name;
        name = "Jack";
        Console.WriteLine(a.Name);

        string s1 = "1";
        ref string s2 = ref s1;
        s2 = "2";
        Console.WriteLine(s1);

        a.Address = new Address(){ City = "New York" };
        var addr = a.Address;
        addr.City = "New Jersey";

        Console.WriteLine(a.Address.City);
    }
}

internal class Person
{
    internal string Name = null!;
    internal int Age { get; set; }

    internal Address Address { get; set; } = null!;
}

internal class Address
{
    internal string City { get; set; } = null!;
}