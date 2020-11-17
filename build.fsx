#I @"./packages/build/FAKE/tools"

#r "FakeLib.dll"
#r "Newtonsoft.Json.dll"

open System.IO
open Fake
open Fake.GitVersionHelper
open Fake.FileUtils

    module ScriptVars =

        type NuGetFeedSettings = { url : string; apiKey : string; userName : string option; userPassword : string option }

        let version() = GitVersion (fun p -> { p with ToolPath = findToolInSubPath "GitVersion.exe" currentDirectory})

        let nugetPublishFeeds() = 
            [
                {
                    url = "https://www.nuget.org/api/v2/package"
                    apiKey =       "NUGET_APIKEY" |> environVarOrFail
                    userName =     None
                    userPassword = None
                }
            ]

Target "TraceVersion" <| fun _ ->
    let version = ScriptVars.version().FullSemVer
    tracefn "Gitversion is %s" version

let ensureNugetFolderStructure folder = 
    folder </> "build" |> mkdir
    ["net35"; "net45"; "net451"; "net452"; "net46"; "net461"; "net462"]
    |> Seq.iter (fun framework -> 
        let frameworkPath = folder </> "lib" </> framework
        frameworkPath |> mkdir
        System.IO.File.WriteAllText(frameworkPath </> "_._", "")
    )    

let updateNuspecVersion version nuspec = 
    XmlPokeInnerText nuspec "package/metadata/version" version

Target "Packages" <| fun _ ->
    let basePath = "artifacts" </> "nuget" </> "Beanstalk.MSBuild.MVC"

    ensureNugetFolderStructure basePath
    
    [
        ("nuget" </> "Beanstalk.MSBuild.MVC.targets", basePath </> "build" </> "Beanstalk.MSBuild.MVC.targets")
        ("nuget" </> "Beanstalk.MSBuild.MVC.nuspec", basePath)
    ]
    |> Seq.iter (fun (src, dst) -> cp_r src dst)
    
    !! "artifacts/nuget/**/*.nuspec"
    |> Seq.iter (updateNuspecVersion <| ScriptVars.version().NuGetVersionV2)

    !! "artifacts/nuget/**/*.nuspec" 
    |> Seq.iter
        (fun nuspecFile ->
            let workingDir = Path.GetDirectoryName nuspecFile
            nuspecFile
            |> NuGetPackDirectly
                (fun n -> 
                    { n with
                        WorkingDir = workingDir
                        OutputPath = "artifacts" </> "nuget"
                        Version = ScriptVars.version().NuGetVersionV2
                    }
                
            ))

let runPaketCommand cmd =
    let paketTool = (findToolFolderInSubPath "paket.exe" (Directory.GetCurrentDirectory() </> ".paket")) </> "paket.exe"

    let execute args =
            let result =
                ExecProcessAndReturnMessages (fun info ->
                    info.FileName <- paketTool
                    info.Arguments <- args) (System.TimeSpan.FromSeconds 120.)
            if result.ExitCode <> 0 || result.Errors.Count > 0 then failwithf "Error during paket.exe command. %s %s\r\n%s" paketTool cmd (toLines result.Errors)
            else result.Messages |> Seq.iter trace
    
    execute cmd

let ensureNugetFeedCredentials (nugetFeedSettings : ScriptVars.NuGetFeedSettings) =
    match nugetFeedSettings.userName, nugetFeedSettings.userPassword with
    | Some usr, Some pass ->
        sprintf "config add-credentials %s --username %s --password %s --authtype ntlm"
            nugetFeedSettings.url
            usr
            pass
        |> runPaketCommand

        nugetFeedSettings
    | _ -> nugetFeedSettings

let uncurry f (a,b) = f a b

Target "PushPackages" <| fun _ ->
    let pushNugetPackage (nugetFeedSettings : ScriptVars.NuGetFeedSettings) package =
        tracefn "Pushing package %s" package
        ProcessHelper.enableProcessTracing <- false
        sprintf "push --url %s --api-key %s %s" nugetFeedSettings.url nugetFeedSettings.apiKey package
        |> runPaketCommand
        ProcessHelper.enableProcessTracing <- true
    
    ProcessHelper.enableProcessTracing <- false                  
    !! "artifacts/nuget/*.nupkg"
    |> Seq.collect
        (fun pkg ->
            ScriptVars.nugetPublishFeeds()
            |> Seq.map ensureNugetFeedCredentials
            |> Seq.map (fun cred -> cred,pkg)
        )
    |> Seq.iter (uncurry pushNugetPackage)
    ProcessHelper.enableProcessTracing <- true


"TraceVersion"
==> "Packages"
==> "PushPackages"

RunTargetOrDefault "PushPackages"
