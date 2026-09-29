namespace SampleMemberDesugarLib;

/// <summary>接口属性 + DIM 默认实现 + 显式实现。去糖后接口携带 get_X/set_X 方法成员。</summary>
public interface IPerson
{
    string FirstName { get; set; }
    string LastName { get; }

    // DIM：带默认实现的计算属性。
    string FullName => FirstName + " " + LastName;
}

public class PersonRecord : IPerson
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
}

public class ExplicitPerson : IPerson
{
    string IPerson.FirstName { get; set; } = "ada";
    string IPerson.LastName => "lovelace";
}
