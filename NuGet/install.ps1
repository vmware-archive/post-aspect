param($installPath, $toolsPath, $package, $project)

$buildProject = Get-MSBuildProject $project.ProjectName

$target = $buildProject.Xml.AddTarget("AspectCompileAfterBuild")
$target.AfterTargets = "AfterBuild"


$task = $target.AddTask("Exec")
$projectDirectory = [IO.Path]::GetDirectoryName($project.FullName)
Push-Location $projectDirectory
$aspectCompiler = Resolve-Path -relative "$toolsPath\Aspectcompiler.exe"
Pop-Location

$task.SetParameter("Command", "`"$aspectCompiler`" `"`$(TargetPath)`"")

$project.Save()