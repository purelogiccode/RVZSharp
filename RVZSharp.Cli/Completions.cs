namespace RVZSharp.Cli;

/// <summary>
/// Shell completion scripts printed by <c>rvzsharp completions &lt;shell&gt;</c>. The scripts
/// are static: the command surface is small and hand-written, so keeping them in sync with
/// <see cref="Program"/> is a documented release checklist item (see docs/usage-cli.md).
/// </summary>
internal static class Completions
{
    private const string Commands = "convert header verify extract info decode completions";
    private const string Formats = "iso gcz wia rvz ciso wbfs tgc";
    private const string CompressionMethods = "none zstd bzip2 lzma lzma2 purge";
    private const string Algorithms = "crc32 md5 sha1";

    /// <summary>Returns the completion script for <paramref name="shell"/>, or null when unsupported.</summary>
    public static string? Get(string shell)
    {
        return shell.ToLowerInvariant() switch
        {
            "bash" => Bash,
            "zsh" => Zsh,
            "fish" => Fish,
            "powershell" or "pwsh" => PowerShell,
            _ => null
        };
    }

    private const string Bash = $$"""
                                  # rvzsharp bash completion. Install:
                                  #   rvzsharp completions bash >> ~/.bashrc
                                  _rvzsharp() {
                                      local cur prev command
                                      COMPREPLY=()
                                      cur="${COMP_WORDS[COMP_CWORD]}"
                                      prev="${COMP_WORDS[COMP_CWORD-1]}"

                                      local commands="{{Commands}}"
                                      local convert_opts="-i --input -o --output -u --user -f --format -s --scrub -b --block_size -c --compression -l --compression_level --chunk-size --no-packing --threads --verify --json -h --help"
                                      local header_opts="-i --input -j --json -b --block_size -c --compression -l --compression_level -h --help"
                                      local verify_opts="-i --input -u --user -a --algorithm --partitions --json -h --help"
                                      local extract_opts="-i --input -o --output -p --partition -s --single -l --list -q --quiet -g --gameonly -h --help"
                                      local decode_opts="--sha1 --threads -h --help"

                                      if [[ $COMP_CWORD -eq 1 ]]; then
                                          COMPREPLY=( $(compgen -W "$commands" -- "$cur") )
                                          return
                                      fi

                                      command="${COMP_WORDS[1]}"
                                      case "$prev" in
                                          -i|--input|-o|--output|-u|--user)
                                              COMPREPLY=( $(compgen -f -- "$cur") )
                                              return
                                              ;;
                                          -s|--single)
                                              # convert's -s is --scrub (a flag); only extract's -s takes a path.
                                              if [[ "$command" == "extract" ]]; then
                                                  COMPREPLY=( $(compgen -f -- "$cur") )
                                              fi
                                              return
                                              ;;
                                          -f|--format)
                                              COMPREPLY=( $(compgen -W "{{Formats}}" -- "$cur") )
                                              return
                                              ;;
                                          -a|--algorithm)
                                              COMPREPLY=( $(compgen -W "{{Algorithms}}" -- "$cur") )
                                              return
                                              ;;
                                          -c|--compression)
                                              COMPREPLY=( $(compgen -W "{{CompressionMethods}}" -- "$cur") )
                                              return
                                              ;;
                                      esac

                                      case "$command" in
                                          convert) COMPREPLY=( $(compgen -W "$convert_opts" -- "$cur") ) ;;
                                          header) COMPREPLY=( $(compgen -W "$header_opts" -- "$cur") ) ;;
                                          verify) COMPREPLY=( $(compgen -W "$verify_opts" -- "$cur") ) ;;
                                          extract) COMPREPLY=( $(compgen -W "$extract_opts" -- "$cur") ) ;;
                                          info) COMPREPLY=( $(compgen -f -- "$cur") ) ;;
                                          decode) COMPREPLY=( $(compgen -W "$decode_opts" -- "$cur") $(compgen -f -- "$cur") ) ;;
                                          completions) COMPREPLY=( $(compgen -W "bash zsh fish powershell" -- "$cur") ) ;;
                                      esac
                                  }
                                  complete -F _rvzsharp rvzsharp
                                  """;

    private const string Zsh = $$"""
                                 #compdef rvzsharp
                                 # rvzsharp zsh completion. Install:
                                 #   rvzsharp completions zsh > "${fpath[1]}/_rvzsharp"
                                 _rvzsharp() {
                                     local -a commands
                                     commands=(
                                         'convert:convert a disc image to another container'
                                         'header:print disc header information'
                                         'verify:hash and verify a disc image'
                                         'extract:list or extract disc files'
                                         'info:legacy alias of header'
                                         'decode:decode any blob to a plain ISO'
                                         'completions:print a shell completion script'
                                     )

                                     if (( CURRENT == 2 )); then
                                         _describe 'command' commands
                                         return
                                     fi

                                     case $words[2] in
                                         convert)
                                             _arguments \
                                                 '(-i --input)'{-i,--input}'[input file]:file:_files' \
                                                 '(-o --output)'{-o,--output}'[output file]:file:_files' \
                                                 '(-u --user)'{-u,--user}'[user folder]:directory:_directories' \
                                                 '(-f --format)'{-f,--format}'[container format]:format:({{Formats}})' \
                                                 '(-s --scrub)'{-s,--scrub}'[scrub junk data]' \
                                                 '(-b --block_size)'{-b,--block_size}'[block size in bytes]:size:' \
                                                 '(-c --compression)'{-c,--compression}'[compression method]:method:({{CompressionMethods}})' \
                                                 '(-l --compression_level)'{-l,--compression_level}'[compression level]:level:' \
                                                 '--chunk-size[chunk size in bytes]:size:' \
                                                 '--no-packing[disable packing]' \
                                                 '--threads[compression/decode threads]:threads:' \
                                                 '--verify[verify the written file]' \
                                                 '--json[print the result as JSON]' \
                                                 '*:file:_files'
                                             ;;
                                        header)
                                            _arguments '(-i --input)'{-i,--input}'[input file]:file:_files' \
                                                '(-j --json)'{-j,--json}'[print JSON]' \
                                                '(-b --block_size)'{-b,--block_size}'[print block size]' \
                                                '(-c --compression)'{-c,--compression}'[print compression]' \
                                                '(-l --compression_level)'{-l,--compression_level}'[print compression level]' \
                                                '(-h --help)'{-h,--help}'[show help]' '*:file:_files'
                                            ;;
                                        verify)
                                            _arguments '(-i --input)'{-i,--input}'[input file]:file:_files' \
                                                '(-u --user)'{-u,--user}'[user folder]:directory:_directories' \
                                                '(-a --algorithm)'{-a,--algorithm}'[digest]:algorithm:({{Algorithms}})' \
                                                '--partitions[verify Wii hash trees]' \
                                                '--json[print the result as JSON]' \
                                                '(-h --help)'{-h,--help}'[show help]' '*:file:_files'
                                            ;;
                                         extract)
                                             _arguments '-i[input file]:file:_files' \
                                                 '-o[output directory]:directory:_directories' \
                                                 '-p[partition]:partition:' '-s[single path]:path:' \
                                                 '-l[list only]' '-q[quiet]' '-g[game partition only]' '*:file:_files'
                                             ;;
                                         info|decode)
                                             _files
                                             ;;
                                         completions)
                                             _values 'shell' bash zsh fish powershell
                                             ;;
                                     esac
                                 }
                                 _rvzsharp "$@"
                                 """;

    private const string Fish = $"""
                                 # rvzsharp fish completion. Install:
                                 #   rvzsharp completions fish > ~/.config/fish/completions/rvzsharp.fish
                                 complete -c rvzsharp -f
                                 complete -c rvzsharp -n '__fish_use_subcommand' -a convert -d 'Convert a disc image'
                                 complete -c rvzsharp -n '__fish_use_subcommand' -a header -d 'Print disc header information'
                                 complete -c rvzsharp -n '__fish_use_subcommand' -a verify -d 'Hash and verify a disc image'
                                 complete -c rvzsharp -n '__fish_use_subcommand' -a extract -d 'List or extract disc files'
                                 complete -c rvzsharp -n '__fish_use_subcommand' -a info -d 'Legacy alias of header'
                                 complete -c rvzsharp -n '__fish_use_subcommand' -a decode -d 'Decode any blob to a plain ISO'
                                 complete -c rvzsharp -n '__fish_use_subcommand' -a completions -d 'Print a shell completion script'

                                 complete -c rvzsharp -n '__fish_seen_subcommand_from convert' -s i -l input -r
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from convert' -s o -l output -r
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from convert' -s u -l user -r
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from convert' -s f -l format -x -a '{Formats}'
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from convert' -s s -l scrub
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from convert' -s b -l block_size -x
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from convert' -s c -l compression -x -a '{CompressionMethods}'
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from convert' -s l -l compression_level -x
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from convert' -l chunk-size -x
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from convert' -l no-packing
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from convert' -l threads -x
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from convert' -l verify
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from convert' -l json

                                 complete -c rvzsharp -n '__fish_seen_subcommand_from header' -s i -l input -r
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from header' -s j -l json
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from header' -s b -l block_size
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from header' -s c -l compression
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from header' -s l -l compression_level

                                 complete -c rvzsharp -n '__fish_seen_subcommand_from verify' -s i -l input -r
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from verify' -s u -l user -r
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from verify' -s a -l algorithm -x -a '{Algorithms}'
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from verify' -l partitions
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from verify' -l json

                                 complete -c rvzsharp -n '__fish_seen_subcommand_from extract' -s i -l input -r
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from extract' -s o -l output -r
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from extract' -s p -l partition -x
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from extract' -s s -l single -r
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from extract' -s l -l list
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from extract' -s q -l quiet
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from extract' -s g -l gameonly

                                 complete -c rvzsharp -n '__fish_seen_subcommand_from decode' -l sha1 -x
                                 complete -c rvzsharp -n '__fish_seen_subcommand_from decode' -l threads -x

                                 complete -c rvzsharp -n '__fish_seen_subcommand_from completions' -x -a 'bash zsh fish powershell'
                                 """;

    private const string PowerShell = """
                                      # rvzsharp PowerShell completion. Install (add to $PROFILE):
                                      #   rvzsharp completions powershell | Out-String | Invoke-Expression
                                      Register-ArgumentCompleter -Native -CommandName rvzsharp -ScriptBlock {
                                          param($wordToComplete, $commandAst, $cursorPosition)

                                          $options = @{
                                              'convert'     = @('-i', '--input', '-o', '--output', '-u', '--user', '-f', '--format',
                                                                '-s', '--scrub', '-b', '--block_size', '-c', '--compression',
                                                                '-l', '--compression_level', '--chunk-size', '--no-packing',
                                                                '--threads', '--verify', '--json', '-h', '--help')
                                              'header'      = @('-i', '--input', '-j', '--json', '-b', '--block_size',
                                                                '-c', '--compression', '-l', '--compression_level', '-h', '--help')
                                              'verify'      = @('-i', '--input', '-u', '--user', '-a', '--algorithm',
                                                                '--partitions', '--json', '-h', '--help')
                                              'extract'     = @('-i', '--input', '-o', '--output', '-p', '--partition',
                                                                '-s', '--single', '-l', '--list', '-q', '--quiet', '-g', '--gameonly',
                                                                '-h', '--help')
                                              'decode'      = @('--sha1', '--threads', '-h', '--help')
                                              'completions' = @('bash', 'zsh', 'fish', 'powershell')
                                          }

                                          $elements = @($commandAst.CommandElements | ForEach-Object { $_.Extent.Text })
                                          $command = if ($elements.Count -ge 2) { $elements[1] } else { $null }

                                          # Complete option values for -f/-c/-a like the POSIX scripts do.
                                          $last = if ($elements.Count -ge 1) { $elements[-1] } else { $null }
                                          $previous = if ($last -and $last.StartsWith('-')) { $last }
                                                      elseif ($elements.Count -ge 2) { $elements[-2] } else { $null }
                                          $values = switch ($previous) {
                                              { $_ -in '-f', '--format' } { @('iso', 'gcz', 'wia', 'rvz', 'ciso', 'wbfs', 'tgc') }
                                              { $_ -in '-c', '--compression' } { @('none', 'zstd', 'bzip2', 'lzma', 'lzma2', 'purge') }
                                              { $_ -in '-a', '--algorithm' } { @('crc32', 'md5', 'sha1') }
                                              default { $null }
                                          }
                                          if ($values) {
                                              $values |
                                                  Where-Object { $_ -like "$wordToComplete*" } |
                                                  ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }
                                              return
                                          }

                                          if ($null -eq $command -or $command.StartsWith('-')) {
                                              @('convert', 'header', 'verify', 'extract', 'info', 'decode', 'completions') |
                                                  Where-Object { $_ -like "$wordToComplete*" } |
                                                  ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }
                                              return
                                          }

                                          $candidates = if ($options.ContainsKey($command)) { $options[$command] } else { @() }
                                          $candidates |
                                              Where-Object { $_ -like "$wordToComplete*" } |
                                              ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }
                                      }
                                      """;
}
