using System.Text;

namespace TeXeed.Core;

public sealed partial class CompileService
{
    private static async Task<string> PrepareLayoutDriverAsync(TexProject project, string build, CancellationToken token)
    {
        // Leave unusual TeX filename syntax to the engine; missing layout metadata means no horizontal crop.
        var input = project.MainPath.Replace('\\', '/');
        if (input.IndexOfAny(['%', '{', '}', '\r', '\n']) >= 0) return project.MainPath;
        var directory = Path.Combine(build, "texeed-driver");
        EnsureOrdinaryDirectory(project.Root, directory);
        var driver = Path.Combine(directory, Path.GetFileName(project.MainPath));
        var text = """
            % Generated preview metadata recorder. The original source remains untouched.
            \makeatletter
            \ifdefined\AddToHook
              \newwrite\TeXeedLayoutStream
              \immediate\openout\TeXeedLayoutStream="\jobname.texeed-layout"
              \immediate\write\TeXeedLayoutStream{TeXeed layout 1}
              \AddToHook{shipout/after}[texeed/layout]{%
                \begingroup
                \dimen0=\oddsidemargin
                \if@twoside\ifodd\c@page\else\dimen0=\evensidemargin\fi\fi
                \advance\dimen0 by 1in \advance\dimen0 by \hoffset
                \dimen2=\textwidth \dimen4=\paperwidth \dimen6=\paperheight
                \dimen0=\dimexpr\dimen0*\mag/1000\relax
                \dimen2=\dimexpr\dimen2*\mag/1000\relax
                \dimen4=\dimexpr\dimen4*\mag/1000\relax
                \dimen6=\dimexpr\dimen6*\mag/1000\relax
                \immediate\write\TeXeedLayoutStream{\number\dimen4:\number\dimen6:\number\dimen0:\number\dimen2}%
                \endgroup
              }
            \fi
            \makeatother
            """ + "\n\\input{\\detokenize{" + input + "}}\n";
        // Stable timestamps let latexmk keep its normal incremental dependency behavior.
        if (!File.Exists(driver) || await File.ReadAllTextAsync(driver, token) != text)
            await File.WriteAllTextAsync(driver, text, new UTF8Encoding(false), token);
        return driver;
    }
}
