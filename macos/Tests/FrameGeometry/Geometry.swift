import CoreVideo
import Foundation

@main struct CheckGeometry {
 static func main() {
  var source: CVPixelBuffer?
  CVPixelBufferCreate(nil,400,200,kCVPixelFormatType_32BGRA,[kCVPixelBufferIOSurfacePropertiesKey:[:]] as CFDictionary,&source)
  let buffer=source!
  CVPixelBufferLockBaseAddress(buffer,[])
  let base=CVPixelBufferGetBaseAddress(buffer)!.assumingMemoryBound(to: UInt8.self)
  let stride=CVPixelBufferGetBytesPerRow(buffer)
  let colors:[[UInt8]]=[[0,0,255,255],[0,255,0,255],[255,0,0,255],[255,255,255,255]]
  for y in 0..<200 { for x in 0..<400 { let c=colors[(y<100 ? 0:2)+(x<200 ? 0:1)];for i in 0..<4 { base[y*stride+x*4+i]=c[i] } } }
  CVPixelBufferUnlockBaseAddress(buffer,[])
  let processor=VideoFrameProcessor()
  var checks=0
  for (rotation,expected) in [(0,[0,1,2,3]),(90,[2,0,3,1]),(180,[3,2,1,0]),(270,[1,3,0,2])] {
   for mirror in [false,true] {
    guard let result=processor.process(buffer,rotation:rotation,mirrored:mirror) else { fatalError("no output") }
    let portrait=rotation==90 || rotation==270
    let points=portrait ? [(500,100),(740,100),(500,600),(740,600)] : [(150,150),(1100,150),(150,570),(1100,570)]
    CVPixelBufferLockBaseAddress(result,.readOnly)
    let p=CVPixelBufferGetBaseAddress(result)!.assumingMemoryBound(to:UInt8.self)
    let step=CVPixelBufferGetBytesPerRow(result)
    let order=mirror ? [expected[1],expected[0],expected[3],expected[2]] : expected
    for (index,point) in points.enumerated() {
     let color=colors[order[index]]
     for channel in 0..<3 {
      let actual=Int(p[point.1*step+point.0*4+channel])
      guard abs(actual-Int(color[channel]))<12 else { fatalError("rotation \(rotation), mirror \(mirror), corner \(index), channel \(channel): \(actual), expected \(color[channel])") }
     }
     checks+=1
    }
    if portrait { guard p[100*step+10*4]==0 && p[100*step+10*4+1]==0 else { fatalError("portrait was stretched or cropped") };checks+=1 }
    CVPixelBufferUnlockBaseAddress(result,.readOnly)
   }
  }
  print("PASS: \(checks) checks for 0/90/180/270° rotation, mirroring, and portrait letterboxing.")
 }
}
