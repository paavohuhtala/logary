﻿module Logary.Targets.Mailgun

open FSharp.Actor
open Logary
open Logary.Target
open Logary.Internals
open Mailgun.Api
open System.Net.Mail

type Domain = string

type MailgunLogaryConf =
  { mailgun    : Configured
    templater  : LogLine -> MailBody
    getOpts    : Domain * LogLine -> SendOpts
    msgFactory : MailgunLogaryConf -> MailBody * SendOpts * LogLine -> Message
    from       : MailAddress
    ``to``     : MailAddress list
    cc         : MailAddress list
    bcc        : MailAddress list
    /// Minimum inclusive level to send e-mail for
    minLevel   : LogLevel
    domain     : Domain }

module internal Impl =

  let templater line =
    let formatter = Formatting.StringFormatter.LevelDatetimeMessagePathNl
    TextBody (formatter.format line)

  let getOpts (domain, line) =
    { SendOpts.Create domain with
        tag = match line.tags with | x :: _ -> Some x | _ -> None }

  let msgFactory conf (body, opts, line) =
    { from        = conf.from
      ``to``      = conf.``to``
      cc          = conf.cc
      bcc         = conf.bcc
      subject     = line.message
      body        = body
      attachments = [] }

  let loop (conf : MailgunLogaryConf) (ri : RuntimeInfo) (inbox : IActor<_>) =
    let rec loop () = async {
      let! msg, _ = inbox.Receive()
      match msg with
      | Log line ->
        if line.level < conf.minLevel then return! loop ()
        let body  = conf.templater line
        let opts  = conf.getOpts (conf.domain, line)
        let msg   = conf.msgFactory conf (body, opts, line)
        let! resp = Messages.send conf.mailgun opts msg
        match resp with
        | Result response ->
          use x = response
          ()
        | x ->
          LogLine.error "unknown response from Mailgun"
          |> LogLine.setData "response" x
          |> Logger.log ri.logger
        return! loop ()
      | Measure msr ->
        return! loop ()
      | Flush ackChan ->
        ackChan.Reply Ack
        return! loop ()
      | Shutdown ackChan ->
        ackChan.Reply Ack
        return ()
      }

    if conf.``to`` = [] then
      Logger.error ri.logger "no `to` configured in Mailgun target"
      async.Return ()
    elif conf.from.Host = "example.com" then
      Logger.error ri.logger "you cannot send e-mail to example.com in Mailgun target"
      async.Return ()
    elif conf.domain = "example.com" then
      Logger.error ri.logger "you cannot send e-mail from example.com in Mailgun target"
      async.Return ()
    else
      loop ()

let empty =
  { mailgun    = { apiKey = "" }
    templater  = Impl.templater
    getOpts    = Impl.getOpts
    msgFactory = Impl.msgFactory
    from       = MailAddress "fixme@example.com"
    ``to``     = []
    cc         = []
    bcc        = []
    minLevel   = Error
    domain     = "example.com"
  }

type MailgunLogaryConf with
  static member Create(from      : MailAddress,
                       ``to``    : MailAddress list,
                       mailgun   : Configured,
                       domain    : Domain,
                       ?minLevel : LogLevel,
                       ?cc : MailAddress list,
                       ?bcc : MailAddress list,
                       ?getOpts : _) =
    { empty with
        mailgun  = mailgun
        domain   = domain
        from     = from
        ``to``   = ``to``
        minLevel = defaultArg minLevel empty.minLevel
        cc       = defaultArg cc empty.cc
        bcc      = defaultArg bcc empty.bcc
        getOpts  = defaultArg getOpts empty.getOpts }

/// Create a new Mailgun target
let create conf = TargetUtils.stdNamedTarget (Impl.loop conf)

/// C# Interop: Create a new Mailgun target
[<CompiledName "Create">]
let create' (conf, name) =
  create conf name

/// Use with LogaryFactory.New( s => s.Target<Mailgun.Builder>() )
type Builder(conf : MailgunLogaryConf, callParent : FactoryApi.ParentCallback<Builder>) =
  member x.MailData(from : MailAddress, ``to`` : MailAddress list, domain : Domain) =
    Builder({ conf with from   = from
                        ``to`` = ``to``
                        domain = domain }, callParent)

  member x.Mailgun(config : Configured) =
    ! (callParent <| Builder({ conf with mailgun = config }, callParent))
    
  new(callParent : FactoryApi.ParentCallback<_>) =
    Builder(empty, callParent)

  interface Logary.Target.FactoryApi.SpecificTargetConf with
    member x.Build name = create conf name
