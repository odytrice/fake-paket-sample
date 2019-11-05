#r "paket: groupref Build"
#load ".fake/build.fsx/intellisense.fsx"

//open Paket.Core

open Fake.Core
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Core.TargetOperators
open System.Linq

open System.IO
open System

open Paket.Domain
open Fake.IO

let Project = {| PackageName = "App.Library"; ProjectFile = "App.Library" </> "App.Library.fsproj" |}

let UpdatePackageVersion (this: Paket.DependenciesFile) groupName packageName (version: string) =

    let tryMatchPackageLine packageNamePredicate (line : string) =
        let tokens = line.Split([|' '|], StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun s -> s.ToLowerInvariant().Trim())
        match List.ofArray tokens with
        | "nuget"::packageName::_ when packageNamePredicate packageName -> Some packageName
        | _ -> None

    let isPackageLine name line = tryMatchPackageLine ((=) name) line |> Option.isSome

    if this.HasPackage(groupName,packageName) then
        let vr = Paket.DependenciesFileParser.parseVersionString version
        let newLines =
            this.Lines
            |> Array.map (fun line ->
                let name = packageName.CompareString
                if isPackageLine name line then
                    let p = this.GetPackage(groupName,packageName)
                    let packageLineString = Paket.DependenciesFileSerializer.packageString Paket.Requirements.PackageRequirementKind.Package packageName vr.VersionRequirement vr.ResolverStrategy p.Settings
                    String(' ', 4) + packageLineString
                else line)

        Paket.DependenciesFile(Paket.DependenciesFileParser.parseDependenciesFile this.FileName false newLines)
    else
        this

let getVersion parameters =
    let arguments = parameters.Context.Arguments
    match arguments with
    | [] -> failwithf "Please specify the versionnumber using: --version VERSIONNUMBER"
    | ["--version"; version] -> Paket.SemVer.Parse version
    | other -> failwithf "Unrecognized options! Please only pass the version number. You gave me: %A" other


Target.create "Pack" (fun parameters ->
    let path = Path.Combine(Directory.GetCurrentDirectory(), "paket.dependencies")
    let dependenciesFile = Paket.DependenciesFile.ReadFromFile(path)

    let group = GroupName "App"
    let packageName = PackageName Project.PackageName
    let version = getVersion parameters
    printf "%A" version
    Shell.mkdir (__SOURCE_DIRECTORY__ </> "nuget")

    let setPackOptions (settings: DotNet.PackOptions) =
        let msBuildParams = { settings.MSBuildParams with Properties = [ "Version", version.ToString() ] }
        { settings with
            Configuration = DotNet.BuildConfiguration.Release
            MSBuildParams = msBuildParams
            OutputPath = Some (__SOURCE_DIRECTORY__ </> "nuget") }

    DotNet.pack setPackOptions Project.ProjectFile)

Target.create "Push" (fun _ ->

    let value = Environment.GetEnvironmentVariable("GITHUB_PUSH_TOKEN")
    let message = "You need to set the environment variable 'GITHUB_PUSH_TOKEN' with a GitHub Access Token https://help.github.com/en/github/authenticating-to-github/creating-a-personal-access-token-for-the-command-line"
    if String.IsNullOrEmpty(value) then failwith message

    //Get All versions for specific Package e.g. nuget/EdelwiessData.Core.0.0.1.nupkg
    let pattern = sprintf "%s*" Project.PackageName
    let files = IO.Directory.GetFiles("nuget", pattern)


    for file in files do
        DotNet.nugetPush
            (fun options ->
                { options with
                    PushParams =
                    { options.PushParams with
                        Source = Some "GitHub" } })
            file
        File.delete(file)
)


Target.create "Update" (fun parameters ->
    let path = Path.Combine(Directory.GetCurrentDirectory(), "paket.dependencies")
    let dependenciesFile = Paket.DependenciesFile.ReadFromFile(path)

    let group = GroupName "App"
    let packageName = PackageName Project.PackageName


    //Increment Version Number
    let version = getVersion parameters
    let newVersion = { version with Patch = version.Patch + 1u; Original = None }
    let file = UpdatePackageVersion dependenciesFile group packageName (newVersion.ToString())
    file.Save()

    Paket.Dependencies.Locate().Install(force = false)
)



Target.create "Clean" (fun _ ->
    !! "src/**/bin"
    ++ "src/**/obj"
    |> Shell.cleanDirs
)

Target.create "Build" (fun _ ->
    DotNet.build (fun (o: DotNet.BuildOptions) -> { o with Configuration = DotNet.BuildConfiguration.Release }) Project.ProjectFile
)

Target.create "All" ignore

"Clean"
  ==> "Build"
  ==> "Pack"
  ==> "Push"
  ==> "Update"
  ==> "All"

Target.runOrDefaultWithArguments "All"
