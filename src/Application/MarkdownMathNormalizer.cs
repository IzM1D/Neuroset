using System.Text;
using System.Text.RegularExpressions;

namespace Neuroset.Application;

public sealed record MarkdownMathFragment(string Marker, string Latex, bool IsDisplay);
public sealed record PreparedMarkdown(string Markdown, IReadOnlyDictionary<string, MarkdownMathFragment> MathFragments);

public static partial class MarkdownMathNormalizer
{
    private static readonly IReadOnlyDictionary<string, string> CommandSymbols = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["alpha"]="α", ["beta"]="β", ["gamma"]="γ", ["delta"]="δ", ["epsilon"]="ε", ["varepsilon"]="ε",
        ["zeta"]="ζ", ["eta"]="η", ["theta"]="θ", ["vartheta"]="ϑ", ["iota"]="ι", ["kappa"]="κ",
        ["lambda"]="λ", ["mu"]="μ", ["nu"]="ν", ["xi"]="ξ", ["pi"]="π", ["rho"]="ρ", ["sigma"]="σ",
        ["tau"]="τ", ["upsilon"]="υ", ["phi"]="φ", ["varphi"]="ϕ", ["chi"]="χ", ["psi"]="ψ", ["omega"]="ω",
        ["Gamma"]="Γ", ["Delta"]="Δ", ["Theta"]="Θ", ["Lambda"]="Λ", ["Xi"]="Ξ", ["Pi"]="Π",
        ["Sigma"]="Σ", ["Phi"]="Φ", ["Psi"]="Ψ", ["Omega"]="Ω",
        ["times"]="×", ["cdot"]="·", ["div"]="÷", ["pm"]="±", ["mp"]="∓", ["le"]="≤", ["leq"]="≤",
        ["ge"]="≥", ["geq"]="≥", ["ne"]="≠", ["neq"]="≠", ["approx"]="≈", ["equiv"]="≡", ["sim"]="∼",
        ["infty"]="∞", ["partial"]="∂", ["nabla"]="∇", ["sum"]="∑", ["prod"]="∏", ["int"]="∫",
        ["in"]="∈", ["notin"]="∉", ["subset"]="⊂", ["subseteq"]="⊆", ["supset"]="⊃", ["supseteq"]="⊇",
        ["cup"]="∪", ["cap"]="∩", ["emptyset"]="∅", ["forall"]="∀", ["exists"]="∃", ["neg"]="¬",
        ["land"]="∧", ["lor"]="∨", ["to"]="→", ["rightarrow"]="→", ["leftarrow"]="←",
        ["Rightarrow"]="⇒", ["Leftarrow"]="⇐", ["leftrightarrow"]="↔", ["mapsto"]="↦",
        ["ldots"]="…", ["cdots"]="⋯", ["vdots"]="⋮", ["ddots"]="⋱",
        ["langle"]="⟨", ["rangle"]="⟩", ["lceil"]="⌈", ["rceil"]="⌉", ["lfloor"]="⌊", ["rfloor"]="⌋",
        ["mid"]="∣", ["vert"]="|", ["Vert"]="‖", ["colon"]=":", ["Re"]="ℜ", ["Im"]="ℑ",
        // Named operators are emitted as text instead of leaking a backslash into
        // the UI. Keeping them in the same table also makes variants such as
        // `\max_p` flow through the normal subscript conversion below.
        ["max"]="max", ["min"]="min", ["sup"]="sup", ["inf"]="inf", ["lim"]="lim",
        ["argmax"]="arg max", ["argmin"]="arg min", ["log"]="log", ["ln"]="ln",
        ["exp"]="exp", ["sin"]="sin", ["cos"]="cos", ["tan"]="tan",
        ["Big"]="", ["Bigl"]="", ["Bigr"]="", ["Bigm"]="", ["Biggl"]="", ["Biggr"]="",
        ["big"]="", ["bigl"]="", ["bigr"]="", ["bigm"]="", ["biggl"]="", ["biggr"]="",
        ["displaystyle"]="", ["limits"]=""
    };

    private static readonly IReadOnlyDictionary<char, char> SuperscriptCharacters = new Dictionary<char, char>
    {
        ['0']='⁰', ['1']='¹', ['2']='²', ['3']='³', ['4']='⁴', ['5']='⁵', ['6']='⁶', ['7']='⁷', ['8']='⁸', ['9']='⁹',
        ['+']='⁺', ['-']='⁻', ['=']='⁼', ['(']='⁽', [')']='⁾', ['n']='ⁿ', ['i']='ⁱ'
    };

    private static readonly IReadOnlyDictionary<char, char> SubscriptCharacters = new Dictionary<char, char>
    {
        ['0']='₀', ['1']='₁', ['2']='₂', ['3']='₃', ['4']='₄', ['5']='₅', ['6']='₆', ['7']='₇', ['8']='₈', ['9']='₉',
        ['+']='₊', ['-']='₋', ['=']='₌', ['(']='₍', [')']='₎', ['a']='ₐ', ['e']='ₑ', ['h']='ₕ',
        ['i']='ᵢ', ['j']='ⱼ', ['k']='ₖ', ['l']='ₗ', ['m']='ₘ', ['n']='ₙ', ['o']='ₒ', ['p']='ₚ',
        ['r']='ᵣ', ['s']='ₛ', ['t']='ₜ', ['u']='ᵤ', ['v']='ᵥ', ['x']='ₓ'
    };

    public static PreparedMarkdown Prepare(string? markdown)
    {
        var normalized=(markdown??string.Empty).Replace("\r\n","\n",StringComparison.Ordinal).Replace('\r','\n');
        var lines=normalized.Split('\n');
        var fragments=new Dictionary<string,MarkdownMathFragment>(StringComparer.Ordinal);
        var result=new StringBuilder(normalized.Length);
        char? fenceCharacter=null;
        var fenceLength=0;
        var fragmentIndex=0;

        for(var index=0;index<lines.Length;index++)
        {
            var line=lines[index];
            var trimmed=line.Trim();
            if(TryReadFence(trimmed,out var currentFence,out var currentFenceLength))
            {
                if(fenceCharacter is null)
                {
                    fenceCharacter=currentFence;
                    fenceLength=currentFenceLength;
                }
                else if(currentFence==fenceCharacter&&currentFenceLength>=fenceLength)
                {
                    fenceCharacter=null;
                    fenceLength=0;
                }
                AppendLine(result,line,index<lines.Length-1);
                continue;
            }

            if(fenceCharacter is null&&TryReadDisplayMath(lines,index,out var latex,out var lastLine))
            {
                var marker=AddFragment(fragments,ref fragmentIndex,latex,true);
                AppendLine(result,marker,index<lines.Length-1);
                index=lastLine;
                continue;
            }

            var prepared=fenceCharacter is null?ReplaceInlineMath(line,fragments,ref fragmentIndex):line;
            AppendLine(result,prepared,index<lines.Length-1);
        }

        return new(result.ToString(),fragments);
    }

    public static string FormatLatex(string? latex)
    {
        var value=(latex??string.Empty).Trim();
        if(value.Length==0)return string.Empty;
        value=value.Replace("\r\n","\n",StringComparison.Ordinal).Replace('\r','\n');
        value=EnvironmentCommandRegex().Replace(value,string.Empty);
        value=value.Replace(@"\left",string.Empty,StringComparison.Ordinal)
            .Replace(@"\right",string.Empty,StringComparison.Ordinal)
            .Replace(@"\,", " ",StringComparison.Ordinal)
            .Replace(@"\;", " ",StringComparison.Ordinal)
            .Replace(@"\:", " ",StringComparison.Ordinal)
            .Replace(@"\!", string.Empty,StringComparison.Ordinal)
            .Replace("~"," ",StringComparison.Ordinal)
            .Replace("&",string.Empty,StringComparison.Ordinal)
            .Replace("\\\\","\n",StringComparison.Ordinal);

        for(var pass=0;pass<12;pass++)
        {
            var before=value;
            value=ReplaceTwoArgumentCommand(value,"frac",(left,right)=>$"{WrapFractionPart(FormatLatex(left))}⁄{WrapFractionPart(FormatLatex(right))}");
            value=ReplaceTwoArgumentCommand(value,"dfrac",(left,right)=>$"{WrapFractionPart(FormatLatex(left))}⁄{WrapFractionPart(FormatLatex(right))}");
            value=ReplaceTwoArgumentCommand(value,"tfrac",(left,right)=>$"{WrapFractionPart(FormatLatex(left))}⁄{WrapFractionPart(FormatLatex(right))}");
            value=ReplaceOneArgumentCommand(value,"boxed",body=>$"⟦ {FormatLatex(body)} ⟧");
            value=ReplaceOneArgumentCommand(value,"sqrt",body=>$"√{WrapRadicand(FormatLatex(body))}");
            value=ReplaceOneArgumentCommand(value,"text",FormatLatex);
            value=ReplaceOneArgumentCommand(value,"textnormal",FormatLatex);
            value=ReplaceOneArgumentCommand(value,"mathrm",FormatLatex);
            value=ReplaceOneArgumentCommand(value,"mathbb",body=>ApplyLetterStyle(FormatLatex(body),BlackboardLetters));
            value=ReplaceOneArgumentCommand(value,"mathcal",body=>ApplyLetterStyle(FormatLatex(body),CalligraphicLetters));
            value=ReplaceOneArgumentCommand(value,"mathfrak",body=>ApplyLetterStyle(FormatLatex(body),FrakturLetters));
            value=ReplaceOneArgumentCommand(value,"mathbf",body=>ApplyLetterStyle(FormatLatex(body),BoldLetters));
            value=ReplaceOneArgumentCommand(value,"boldsymbol",body=>ApplyLetterStyle(FormatLatex(body),BoldLetters));
            value=ReplaceOneArgumentCommand(value,"mathsf",FormatLatex);
            value=ReplaceOneArgumentCommand(value,"mathtt",FormatLatex);
            value=ReplaceOneArgumentCommand(value,"operatorname",FormatLatex);
            value=BracedSuperscriptRegex().Replace(value,match=>ToScript(match.Groups[1].Value,SuperscriptCharacters,'^'));
            value=BracedSubscriptRegex().Replace(value,match=>ToScript(match.Groups[1].Value,SubscriptCharacters,'_'));
            value=SimpleSuperscriptRegex().Replace(value,match=>ToScript(match.Groups[1].Value,SuperscriptCharacters,'^'));
            value=SimpleSubscriptRegex().Replace(value,match=>ToScript(match.Groups[1].Value,SubscriptCharacters,'_'));
            if(value==before)break;
        }

        // Replace longer names first so a command such as `\\infty` is not
        // accidentally split by the shorter `\\in` entry.
        foreach(var command in CommandSymbols.OrderByDescending(command=>command.Key.Length))
            value=value.Replace("\\"+command.Key,command.Value,StringComparison.Ordinal);
        value=UnknownCommandRegex().Replace(value,match=>match.Groups[1].Value);
        value=value.Replace('{','(').Replace('}',')');

        var normalizedLines=value.Split('\n')
            .Select(line=>WhitespaceRegex().Replace(line," ").Trim())
            .Where(line=>line.Length>0);
        return string.Join(Environment.NewLine,normalizedLines);
    }

    private static bool TryReadDisplayMath(string[] lines,int index,out string latex,out int lastLine)
    {
        latex=string.Empty;
        lastLine=index;
        var trimmed=lines[index].Trim();

        if(trimmed.Length>4&&trimmed.StartsWith("$$",StringComparison.Ordinal)&&trimmed.EndsWith("$$",StringComparison.Ordinal))
        {
            latex=trimmed[2..^2].Trim();
            return latex.Length>0;
        }
        if(trimmed.Length>4&&trimmed.StartsWith("\\[",StringComparison.Ordinal)&&trimmed.EndsWith("\\]",StringComparison.Ordinal))
        {
            latex=trimmed[2..^2].Trim();
            return latex.Length>0;
        }
        if(trimmed.Length>2&&trimmed[0]=='['&&trimmed[^1]==']'&&LooksLikeMath(trimmed[1..^1]))
        {
            latex=trimmed[1..^1].Trim();
            return true;
        }

        var closing=trimmed switch
        {
            "$$"=>"$$",
            "\\["=>"\\]",
            "["=>"]",
            _=>null
        };
        if(closing is null)return false;

        var body=new StringBuilder();
        for(var candidate=index+1;candidate<lines.Length;candidate++)
        {
            if(lines[candidate].Trim().Equals(closing,StringComparison.Ordinal))
            {
                latex=body.ToString().Trim();
                lastLine=candidate;
                return latex.Length>0&&(trimmed!="["||LooksLikeMath(latex));
            }
            if(body.Length>0)body.Append('\n');
            body.Append(lines[candidate]);
        }
        return false;
    }

    private static string ReplaceInlineMath(string line,Dictionary<string,MarkdownMathFragment> fragments,ref int fragmentIndex)
    {
        var nextIndex=fragmentIndex;
        line=InlineParenthesesRegex().Replace(line,match=>AddFragment(fragments,ref nextIndex,match.Groups[1].Value,false));
        line=InlineDollarRegex().Replace(line,match=>
        {
            var body=match.Groups[1].Value;
            return LooksLikeMath(body)?AddFragment(fragments,ref nextIndex,body,false):match.Value;
        });
        fragmentIndex=nextIndex;
        return line;
    }

    private static string AddFragment(Dictionary<string,MarkdownMathFragment> fragments,ref int index,string latex,bool display)
    {
        var marker=$"NSMATHFRAGMENT{++index:000000}TOKEN";
        fragments[marker]=new(marker,latex.Trim(),display);
        return marker;
    }

    private static bool TryReadFence(string line,out char character,out int length)
    {
        character='\0';
        length=0;
        if(line.Length<3||line[0] is not ('\u0060' or '~'))return false;
        character=line[0];
        while(length<line.Length&&line[length]==character)length++;
        return length>=3;
    }

    private static bool LooksLikeMath(string value)
    {
        var trimmed=value.Trim();
        if(trimmed.Length==0)return false;
        return trimmed.Contains('\\')||trimmed.IndexOfAny(['=','^','_','∑','∫','√','≤','≥','±','×','÷'])>=0||
            Regex.IsMatch(trimmed,@"\d\s*[+\-*/]\s*\d",RegexOptions.CultureInvariant);
    }

    private static string ReplaceOneArgumentCommand(string source,string command,Func<string,string> replacement)
    {
        var search="\\"+command;
        var index=0;
        while((index=source.IndexOf(search,index,StringComparison.Ordinal))>=0)
        {
            var argumentStart=index+search.Length;
            if(!TryReadBracedArgument(source,argumentStart,out var body,out var end))
            {
                index=argumentStart;
                continue;
            }
            source=source[..index]+replacement(body)+source[end..];
        }
        return source;
    }

    private static string ReplaceTwoArgumentCommand(string source,string command,Func<string,string,string> replacement)
    {
        var search="\\"+command;
        var index=0;
        while((index=source.IndexOf(search,index,StringComparison.Ordinal))>=0)
        {
            var firstStart=index+search.Length;
            if(!TryReadBracedArgument(source,firstStart,out var first,out var firstEnd)||
                !TryReadBracedArgument(source,firstEnd,out var second,out var secondEnd))
            {
                index=firstStart;
                continue;
            }
            source=source[..index]+replacement(first,second)+source[secondEnd..];
        }
        return source;
    }

    private static bool TryReadBracedArgument(string source,int start,out string body,out int end)
    {
        body=string.Empty;
        end=start;
        while(start<source.Length&&char.IsWhiteSpace(source[start]))start++;
        if(start>=source.Length||source[start]!='{')return false;
        var depth=0;
        for(var index=start;index<source.Length;index++)
        {
            if(source[index]=='{')depth++;
            else if(source[index]=='}'&&--depth==0)
            {
                body=source[(start+1)..index];
                end=index+1;
                return true;
            }
        }
        return false;
    }

    private static string WrapFractionPart(string value)=>value.IndexOfAny(['+','-','=',' '])>=0?$"({value})":value;
    private static string WrapRadicand(string value)=>value.Length==1||value.All(char.IsLetterOrDigit)?value:$"({value})";

    private static string ApplyLetterStyle(string value,IReadOnlyDictionary<char,string> alphabet)
    {
        if(value.Length==0)return value;
        var builder=new StringBuilder(value.Length);
        foreach(var character in value)
            builder.Append(alphabet.TryGetValue(character,out var styled)?styled:character.ToString());
        return builder.ToString();
    }

    private static readonly IReadOnlyDictionary<char,string> BlackboardLetters = CreateAlphabet(
        "𝔸𝔹ℂ𝔻𝔼𝔽𝔾ℍ𝕀𝕁𝕂𝕃𝕄ℕ𝕆ℙℚℝ𝕊𝕋𝕌𝕍𝕎𝕏𝕐ℤ",
        "𝕒𝕓𝕔𝕕𝕖𝕗𝕘𝕙𝕚𝕛𝕜𝕝𝕞𝕟𝕠𝕡𝕢𝕣𝕤𝕥𝕦𝕧𝕨𝕩𝕪𝕫");
    private static readonly IReadOnlyDictionary<char,string> CalligraphicLetters = CreateAlphabet(
        "𝒜ℬ𝒞𝒟ℰℱ𝒢ℋℐ𝒥𝒦ℒℳ𝒩𝒪𝒫𝒬ℛ𝒮𝒯𝒰𝒱𝒲𝒳𝒴𝒵",
        "𝒶𝒷𝒸𝒹ℯ𝒻ℊ𝒽𝒾𝒿𝓀𝓁𝓂𝓃ℴ𝓅𝓆𝓇𝓈𝓉𝓊𝓋𝓌𝓍𝓎𝓏");
    private static readonly IReadOnlyDictionary<char,string> FrakturLetters = CreateAlphabet(
        "𝔄𝔅ℭ𝔇𝔈𝔉𝔊ℌℑ𝔍𝔎𝔏𝔐𝔑𝔒𝔓𝔔ℜ𝔖𝔗𝔘𝔙𝔚𝔛𝔜ℨ",
        "𝔞𝔟𝔠𝔡𝔢𝔣𝔤𝔥𝔦𝔧𝔨𝔩𝔪𝔫𝔬𝔭𝔮𝔯𝔰𝔱𝔲𝔳𝔴𝔵𝔶𝔷");
    private static readonly IReadOnlyDictionary<char,string> BoldLetters = CreateAlphabet(
        "𝐀𝐁𝐂𝐃𝐄𝐅𝐆𝐇𝐈𝐉𝐊𝐋𝐌𝐍𝐎𝐏𝐐𝐑𝐒𝐓𝐔𝐕𝐖𝐗𝐘𝐙",
        "𝐚𝐛𝐜𝐝𝐞𝐟𝐠𝐡𝐢𝐣𝐤𝐥𝐦𝐧𝐨𝐩𝐪𝐫𝐬𝐭𝐮𝐯𝐰𝐱𝐲𝐳");

    private static IReadOnlyDictionary<char,string> CreateAlphabet(string upper,string lower)
    {
        var alphabet=new Dictionary<char,string>(52);
        var upperLetters=System.Globalization.StringInfo.ParseCombiningCharacters(upper);
        var lowerLetters=System.Globalization.StringInfo.ParseCombiningCharacters(lower);
        for(var index=0;index<26;index++)
        {
            alphabet[(char)('A'+index)]=upper.Substring(upperLetters[index],index<25?upperLetters[index+1]-upperLetters[index]:upper.Length-upperLetters[index]);
            alphabet[(char)('a'+index)]=lower.Substring(lowerLetters[index],index<25?lowerLetters[index+1]-lowerLetters[index]:lower.Length-lowerLetters[index]);
        }
        return alphabet;
    }

    private static string ToScript(string value,IReadOnlyDictionary<char,char> map,char fallback)
    {
        var builder=new StringBuilder(value.Length);
        foreach(var character in value)
        {
            if(!map.TryGetValue(character,out var mapped))return $"{fallback}({value})";
            builder.Append(mapped);
        }
        return builder.ToString();
    }

    private static void AppendLine(StringBuilder builder,string line,bool appendNewLine)
    {
        builder.Append(line);
        if(appendNewLine)builder.Append('\n');
    }

    [GeneratedRegex(@"\\begin\{(?:aligned|align\*?|equation\*?|gathered|cases|matrix|pmatrix|bmatrix)\}|\\end\{(?:aligned|align\*?|equation\*?|gathered|cases|matrix|pmatrix|bmatrix)\}",RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentCommandRegex();
    [GeneratedRegex(@"\\\((.+?)\\\)",RegexOptions.CultureInvariant)]
    private static partial Regex InlineParenthesesRegex();
    [GeneratedRegex(@"(?<!\\)(?<!\$)\$(?!\$)(.+?)(?<!\\)\$(?!\$)",RegexOptions.CultureInvariant)]
    private static partial Regex InlineDollarRegex();
    [GeneratedRegex(@"\^\{([^{}]+)\}",RegexOptions.CultureInvariant)]
    private static partial Regex BracedSuperscriptRegex();
    [GeneratedRegex(@"_\{([^{}]+)\}",RegexOptions.CultureInvariant)]
    private static partial Regex BracedSubscriptRegex();
    [GeneratedRegex(@"\^([A-Za-z0-9+\-=()])",RegexOptions.CultureInvariant)]
    private static partial Regex SimpleSuperscriptRegex();
    [GeneratedRegex(@"_([A-Za-z0-9+\-=()])",RegexOptions.CultureInvariant)]
    private static partial Regex SimpleSubscriptRegex();
    [GeneratedRegex(@"\\([A-Za-z]+)",RegexOptions.CultureInvariant)]
    private static partial Regex UnknownCommandRegex();
    [GeneratedRegex(@"[ \t]+",RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
