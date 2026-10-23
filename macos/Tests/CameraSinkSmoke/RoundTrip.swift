import CoreMediaIO
import CoreVideo
import Foundation

func ids(_ object: CMIOObjectID, _ selector: Int) -> [CMIOObjectID] {
 var a = CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(selector),mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal),mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
 var size: UInt32=0; var used: UInt32=0
 guard CMIOObjectGetPropertyDataSize(object,&a,0,nil,&size)==noErr,size>0 else { return [] }
 var values=[CMIOObjectID](repeating:0,count:Int(size)/4)
 guard CMIOObjectGetPropertyData(object,&a,0,nil,size,&used,&values)==noErr else { return [] }
 return values
}
func name(_ object: CMIOObjectID) -> String? {
 var a=CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(kCMIOObjectPropertyName),mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal),mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
 var used: UInt32=0; var value: Unmanaged<CFString>?
 guard CMIOObjectGetPropertyData(object,&a,0,nil,UInt32(MemoryLayout.size(ofValue:value)),&used,&value)==noErr else { return nil }
 return value?.takeRetainedValue() as String?
}
@main struct RoundTrip {
 static func main() {
  let devices=ids(CMIOObjectID(kCMIOObjectSystemObject),kCMIOHardwarePropertyDevices)
  guard let device=devices.first(where:{name($0)=="OBS Virtual Camera"}),let source=ids(device,kCMIODevicePropertyStreams).first(where:{name($0)?.hasSuffix("Source")==true}) else { fatalError("camera source missing") }
  var copied: Unmanaged<CMSimpleQueue>?
  guard CMIOStreamCopyBufferQueue(source,{_,_,_ in},nil,&copied)==noErr,let copied else { fatalError("source queue") }
  let queue=copied.takeRetainedValue()
  guard CMIODeviceStartStream(device,source)==noErr else { fatalError("start source") }
  defer { CMIODeviceStopStream(device,source) }
  let sink=CameraSink()
  guard sink.attach() else { fatalError("attach: \(sink.lastError ?? "?")") }
  defer { sink.detach() }
  var buffer: CVPixelBuffer?
  CVPixelBufferCreate(nil,1280,720,kCVPixelFormatType_32BGRA,[kCVPixelBufferIOSurfacePropertiesKey:[:]] as CFDictionary,&buffer)
  guard let buffer else { fatalError("buffer") }
  CVPixelBufferLockBaseAddress(buffer,[])
  let base=CVPixelBufferGetBaseAddress(buffer)!.assumingMemoryBound(to: UInt8.self)
  let stride=CVPixelBufferGetBytesPerRow(buffer)
  for y in 0..<720 { for x in 0..<1280 { let p=base+y*stride+x*4; p[0]=200;p[1]=32;p[2]=180;p[3]=255 } }
  CVPixelBufferUnlockBaseAddress(buffer,[])
  var received=0
  for _ in 0..<60 {
   sink.send(buffer,pts:.invalid)
   RunLoop.current.run(until:Date().addingTimeInterval(1.0/30))
   while let ptr=CMSimpleQueueDequeue(queue) {
    let sample=Unmanaged<CMSampleBuffer>.fromOpaque(ptr).takeRetainedValue()
    if let image=CMSampleBufferGetImageBuffer(sample),CVPixelBufferGetWidth(image)==1280,CVPixelBufferGetPixelFormatType(image)==kCVPixelFormatType_422YpCbCr8 {
     CVPixelBufferLockBaseAddress(image,.readOnly)
     let p=CVPixelBufferGetBaseAddress(image)!.assumingMemoryBound(to:UInt8.self)
     if p[0]>150 && p[1]<130 && p[2]>150 { received+=1 }
     CVPixelBufferUnlockBaseAddress(image,.readOnly)
    }
   }
  }
  guard received>10 else { fatalError("received only \(received) matching frames out of \(sink.framesSent) sent") }
  let sent=sink.framesSent
  sink.detach()
  var placeholderReceived=false
  for _ in 0..<45 {
   RunLoop.current.run(until:Date().addingTimeInterval(1.0/30))
   while let ptr=CMSimpleQueueDequeue(queue) {
    let sample=Unmanaged<CMSampleBuffer>.fromOpaque(ptr).takeRetainedValue()
    if let image=CMSampleBufferGetImageBuffer(sample),CVPixelBufferGetPixelFormatType(image)==kCVPixelFormatType_32BGRA {
     placeholderReceived=true
    }
   }
  }
  guard placeholderReceived else { fatalError("OBS did not restore its placeholder after sender stopped") }
  print("PASS: \(received) matching synthetic frames read back from OBS Virtual Camera; producer sent \(sent). Placeholder restored after stopping.")
 }
}
